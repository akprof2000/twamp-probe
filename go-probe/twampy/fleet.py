#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2013-2026 Nokia
# Доработка twampy-fleet (c) 2026 — массовый запуск экземпляров.

"""Масштабируемый запуск TWAMP-light: флот рефлекторов и отправителей.

Исходный twampy привязывает один UDP-сокет к фиксированному порту на каждый
режим, поэтому вторая копия падает с конфликтом порта. Здесь реализован запуск
до ~10000 экземпляров:

* ``responder-fleet`` — один процесс на asyncio биндит N последовательных
  портов и отражает пакеты на всех сразу (масштабируется на Windows через
  IOCP/ProactorEventLoop, без 10000 потоков/процессов);
* ``sender-fleet`` — N отправителей, каждый со своего исходного порта, шлют
  тестовые потоки к мишеням и собирают агрегированную статистику;
* ``spawn`` — лаунчер: делит диапазон портов между K отдельными ОС-процессами
  (для полной изоляции, когда это нужно).

Форматы пакетов TWAMP-light полностью совпадают с классическими режимами
twampy (см. TwampySessionReflector / TwampySessionSender в __main__).
"""

import asyncio
import math
import random
import socket
import struct
import subprocess
import sys

from twampy.core import ALLBITS, TIMEOFFSET, log, now, parse_addr, time_ntp2py, zeros

# Сколько секунд ждать «хвост» ответов после отправки последнего пакета.
TAIL_TIMEOUT = 5.0


def build_padmix(padding, ipversion):
    """Набор размеров паддинга (совместимо с классическими режимами twampy)."""
    if padding != -1:
        return [padding]
    if ipversion == 6:
        return [0, 0, 0, 0, 0, 0, 0, 514, 514, 514, 514, 1438]
    return [8, 8, 8, 8, 8, 8, 8, 534, 534, 534, 534, 1458]


def _apply_sockopts(sock, tos, ttl, ipversion):
    """Best-effort установка TOS/TTL на уже привязанный UDP-сокет."""
    try:
        if ipversion == 6:
            sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_TCLASS, tos)
            sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_UNICAST_HOPS, ttl)
        else:
            sock.setsockopt(socket.IPPROTO_IP, socket.IP_TOS, tos)
            sock.setsockopt(socket.IPPROTO_IP, socket.IP_TTL, ttl)
    except OSError as e:
        log.debug("не удалось выставить сокет-опции: %s", e)


def _family(ipversion):
    return socket.AF_INET6 if ipversion == 6 else socket.AF_INET


def _bind_host(host, ipversion):
    """Нормализовать хост для asyncio.create_datagram_endpoint.

    Классический twampy биндит сырой сокет к "" (INADDR_ANY). Но asyncio делает
    getaddrinfo("") и на Windows выбирает конкретный интерфейс, из-за чего sendto
    на loopback падает с WinError 1214. Поэтому пустой хост заменяем на явный
    wildcard: 0.0.0.0 для IPv4 и :: для IPv6.
    """
    if host == "":
        return "::" if ipversion == 6 else "0.0.0.0"
    return host


def _fmt_failed(failed):
    """Короткая сводка по портам, которые не удалось привязать."""
    shown = ", ".join(str(p) for p, _ in failed[:5])
    more = "" if len(failed) <= 5 else f" (и ещё {len(failed) - 5})"
    return f"{shown}{more}"


#############################################################################
# Рефлектор (responder) — флот
#############################################################################


class _ReflectorCounters:
    __slots__ = ("reflected", "errors")

    def __init__(self):
        self.reflected = 0
        self.errors = 0


class _ReflectorProtocol(asyncio.DatagramProtocol):
    """Один UDP-порт TWAMP-light рефлектора.

    Логика идентична TwampySessionReflector.run: на каждый принятый запрос
    формируется ответ с меткой приёма/отправки и порядковым номером сессии.
    Сессии различаются по адресу источника и сбрасываются по таймауту 30с
    либо при получении sseq==0.
    """

    __slots__ = ("padmix", "counters", "index", "reset", "transport")

    def __init__(self, padmix, counters):
        self.padmix = padmix
        self.counters = counters
        self.index = {}
        self.reset = {}
        self.transport = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, address):
        if len(data) < 14:
            # слишком короткий пакет — нечего отражать
            return

        t2 = now()
        sec = int(TIMEOFFSET + t2)  # секунды с 1-JAN-1900
        msec = int((t2 - int(t2)) * ALLBITS)  # 32-битная дробная часть секунды

        sseq = struct.unpack("!I", data[0:4])[0]

        idx = 0
        prev = self.index.get(address)
        if prev is None:
            pass  # новая сессия (новый адрес/порт)
        elif self.reset[address] < t2:
            pass  # таймаут сессии (30с) → rseq:=0
        elif sseq == 0:
            pass  # получен sseq==0 → rseq:=0
        else:
            idx = prev

        rdata = struct.pack("!L2I2H2I", idx, sec, msec, 0x001, 0, sec, msec)
        pbytes = zeros(self.padmix[int(len(self.padmix) * random.random())])
        try:
            self.transport.sendto(rdata + data[0:14] + pbytes, address)
        except Exception as e:  # noqa: BLE001 — сокет мог закрыться на остановке
            log.debug("ошибка отправки ответа: %s", e)
            self.counters.errors += 1
            return

        self.index[address] = idx + 1
        self.reset[address] = t2 + 30  # таймаут сессии 30с
        self.counters.reflected += 1

    def error_received(self, exc):
        self.counters.errors += 1
        log.debug("error_received: %s", exc)


async def _run_responder_fleet(host, base_port, count, step, tos, ttl, ipversion, padding, quiet, report_interval):
    loop = asyncio.get_running_loop()
    padmix = build_padmix(padding, ipversion)
    counters = _ReflectorCounters()
    family = _family(ipversion)
    bind_host = _bind_host(host, ipversion)

    transports = []
    failed = []
    t_bind = now()

    for i in range(count):
        port = base_port + i * step
        try:
            transport, _proto = await loop.create_datagram_endpoint(
                lambda: _ReflectorProtocol(padmix, counters),
                local_addr=(bind_host, port),
                family=family,
            )
        except OSError as e:
            failed.append((port, e))
            continue
        sock = transport.get_extra_info("socket")
        if sock is not None:
            _apply_sockopts(sock, tos, ttl, ipversion)
        transports.append(transport)
        if not quiet and len(transports) % 1000 == 0:
            print(f"  ... привязано {len(transports)}/{count} портов", flush=True)

    bound = len(transports)
    elapsed = now() - t_bind
    hostdisp = host or "*"
    last_port = base_port + (count - 1) * step
    print(
        f"Рефлекторный флот: привязано {bound}/{count} портов "
        f"[{hostdisp}]:{base_port}..{last_port} (шаг {step}) за {elapsed:.2f}с",
        flush=True,
    )
    if failed:
        print(f"  не удалось привязать {len(failed)} портов: {_fmt_failed(failed)}", flush=True)
    if bound == 0:
        for t in transports:
            t.close()
        raise SystemExit("Не удалось привязать ни одного порта — флот не запущен.")

    print("Готов отражать пакеты. Ctrl+C для остановки.", flush=True)

    try:
        while True:
            await asyncio.sleep(report_interval)
            if not quiet:
                print(
                    f"[статистика] отражено пакетов: {counters.reflected}, ошибок: {counters.errors}",
                    flush=True,
                )
    finally:
        for t in transports:
            t.close()
        print(
            f"Рефлекторный флот остановлен. Всего отражено: {counters.reflected}, ошибок: {counters.errors}.",
            flush=True,
        )


#############################################################################
# Отправитель (sender) — флот
#############################################################################


class _SenderAgg:
    """Агрегированная статистика по всему флоту отправителей."""

    __slots__ = ("sent", "received", "errors", "minRT", "maxRT", "sumRT")

    def __init__(self):
        self.sent = 0
        self.received = 0
        self.errors = 0
        self.minRT = None
        self.maxRT = None
        self.sumRT = 0.0

    def add_rtt(self, rtt):
        self.received += 1
        self.sumRT += rtt
        if self.minRT is None or rtt < self.minRT:
            self.minRT = rtt
        if self.maxRT is None or rtt > self.maxRT:
            self.maxRT = rtt


class _SenderProtocol(asyncio.DatagramProtocol):
    """Один отправитель TWAMP-light: шлёт `count` пакетов к мишени.

    Формат исходящего пакета и вычисление RTT совпадают с
    TwampySessionSender.run.
    """

    __slots__ = ("loop", "target", "count", "interval", "padmix", "agg", "on_done", "transport", "idx", "done", "_tail")

    def __init__(self, loop, target, count, interval, padmix, agg, on_done):
        self.loop = loop
        self.target = target
        self.count = count
        self.interval = interval  # секунды
        self.padmix = padmix
        self.agg = agg
        self.on_done = on_done
        self.transport = None
        self.idx = 0
        self.done = False
        self._tail = None

    def connection_made(self, transport):
        self.transport = transport
        self._send_one()

    def _send_one(self):
        if self.done:
            return
        if self.idx >= self.count:
            # всё отправлено — ждём «хвост» ответов, затем завершаемся
            self._tail = self.loop.call_later(TAIL_TIMEOUT, self._finish)
            return

        t1 = now()
        data = struct.pack("!L2IH", self.idx, int(TIMEOFFSET + t1), int((t1 - int(t1)) * ALLBITS), 0x3FFF)
        pbytes = zeros(self.padmix[int(len(self.padmix) * random.random())])
        try:
            self.transport.sendto(data + pbytes, self.target)
            self.agg.sent += 1
        except Exception as e:  # noqa: BLE001
            log.debug("ошибка отправки: %s", e)
            self.agg.errors += 1

        self.idx += 1
        self.loop.call_later(self.interval, self._send_one)

    def datagram_received(self, data, address):
        t4 = now()
        if len(data) < 36:
            return
        t3 = time_ntp2py(data[4:12])
        t2 = time_ntp2py(data[16:24])
        t1 = time_ntp2py(data[28:36])
        rtt = max(0.0, 1000 * (t4 - t1 + t2 - t3))  # round-trip, мс
        self.agg.add_rtt(rtt)
        if self.agg.received and self.agg.received % 100000 == 0:
            log.debug("получено ответов: %d", self.agg.received)

    def error_received(self, exc):
        self.agg.errors += 1
        log.debug("error_received: %s", exc)

    def _finish(self):
        if self.done:
            return
        self.done = True
        if self._tail is not None:
            self._tail.cancel()
        if self.transport is not None:
            self.transport.close()
        self.on_done()


def _print_sender_summary(agg, senders, packets):
    total = senders * packets
    loss = total - agg.received
    loss_pct = (100.0 * loss / total) if total else 0.0
    avg = (agg.sumRT / agg.received) if agg.received else 0.0
    print("===============================================================================", flush=True)
    print(f"Флот отправителей: {senders} шт. x {packets} пакетов = {total} ожидалось", flush=True)
    print("-------------------------------------------------------------------------------", flush=True)
    print(f"  Отправлено:  {agg.sent}", flush=True)
    print(f"  Получено:    {agg.received}", flush=True)
    print(f"  Потери:      {loss}  ({loss_pct:.2f}%)", flush=True)
    print(f"  Ошибки TX:   {agg.errors}", flush=True)
    if agg.received:
        print(
            f"  RTT (мс):    min={agg.minRT:.3f}  avg={avg:.3f}  max={agg.maxRT:.3f}",
            flush=True,
        )
    else:
        print("  RTT: нет ответов (100% потерь)", flush=True)
    print("===============================================================================", flush=True)


async def _run_sender_fleet(
    host,
    base_port,
    senders,
    step,
    target_ip,
    target_base_port,
    target_step,
    packets,
    interval_ms,
    tos,
    ttl,
    ipversion,
    padding,
    quiet,
):
    loop = asyncio.get_running_loop()
    padmix = build_padmix(padding, ipversion)
    agg = _SenderAgg()
    family = _family(ipversion)
    bind_host = _bind_host(host, ipversion)
    interval = float(interval_ms) / 1000.0

    done_event = asyncio.Event()
    remaining = senders

    def on_done():
        nonlocal remaining
        remaining -= 1
        if remaining <= 0:
            done_event.set()

    transports = []
    failed = []
    t_bind = now()

    for i in range(senders):
        src_port = base_port + i * step
        dst = (target_ip, target_base_port + i * target_step)
        try:
            transport, _proto = await loop.create_datagram_endpoint(
                lambda dst=dst: _SenderProtocol(loop, dst, packets, interval, padmix, agg, on_done),
                local_addr=(bind_host, src_port),
                family=family,
            )
        except OSError as e:
            failed.append((src_port, e))
            remaining -= 1
            continue
        sock = transport.get_extra_info("socket")
        if sock is not None:
            _apply_sockopts(sock, tos, ttl, ipversion)
        transports.append(transport)
        if not quiet and len(transports) % 1000 == 0:
            print(f"  ... запущено {len(transports)}/{senders} отправителей", flush=True)

    bound = len(transports)
    elapsed = now() - t_bind
    hostdisp = host or "*"
    print(
        f"Флот отправителей: запущено {bound}/{senders} с исходных портов "
        f"[{hostdisp}]:{base_port}.. -> {target_ip}:{target_base_port}.. за {elapsed:.2f}с",
        flush=True,
    )
    if failed:
        print(f"  не удалось привязать {len(failed)} исходных портов: {_fmt_failed(failed)}", flush=True)
    if bound == 0:
        raise SystemExit("Не удалось запустить ни одного отправителя.")

    # если все, кто должен был работать, отвалились на старте — не зависаем
    if remaining <= 0:
        done_event.set()

    print(f"Отправка {packets} пакетов/шт с интервалом {interval_ms}мс. Ctrl+C для остановки.", flush=True)

    try:
        await done_event.wait()
    finally:
        for t in transports:
            t.close()
        _print_sender_summary(agg, senders, packets)


#############################################################################
# Точки входа режимов fleet (вызываются из __main__)
#############################################################################


def responder_fleet(args):
    host, base_port, ipv = parse_addr(args.near_end, 20001)
    ipversion = 6 if ipv == 6 else 4
    try:
        asyncio.run(
            _run_responder_fleet(
                host=host,
                base_port=base_port,
                count=args.count,
                step=args.step,
                tos=args.tos,
                ttl=args.ttl,
                ipversion=ipversion,
                padding=args.padding,
                quiet=args.quiet,
                report_interval=args.report,
            )
        )
    except KeyboardInterrupt:
        pass


def sender_fleet(args):
    host, base_port, sipv = parse_addr(args.near_end, 20000)
    target_ip, target_base_port, ripv = parse_addr(args.far_end, 20001)
    ipversion = 6 if (sipv == 6 or ripv == 6) else 4
    try:
        asyncio.run(
            _run_sender_fleet(
                host=host,
                base_port=base_port,
                senders=args.count,
                step=args.step,
                target_ip=target_ip,
                target_base_port=target_base_port,
                target_step=args.target_step,
                packets=args.packets,
                interval_ms=args.interval,
                tos=args.tos,
                ttl=args.ttl,
                ipversion=ipversion,
                padding=args.padding,
                quiet=args.quiet,
            )
        )
    except KeyboardInterrupt:
        pass


#############################################################################
# Лаунчер отдельных процессов (spawn)
#############################################################################


def _split_counts(total, procs):
    """Разбить total на procs частей максимально равномерно."""
    per = math.ceil(total / procs)
    parts = []
    left = total
    for _ in range(procs):
        if left <= 0:
            break
        n = min(per, left)
        parts.append(n)
        left -= n
    return parts


def spawn(args):
    """Запустить несколько отдельных ОС-процессов, поделив диапазон портов."""
    parts = _split_counts(args.count, args.procs)
    if not parts:
        raise SystemExit("Нечего запускать: count/procs дают 0 процессов.")

    step = args.step
    base = f"{args.host}" if args.host else ""
    children = []
    offset = 0

    print(f"spawn: режим={args.mode}, всего={args.count}, процессов={len(parts)}, шаг портов={step}", flush=True)

    for idx, n in enumerate(parts):
        sub_base = args.base_port + offset * step
        near = f"{base}:{sub_base}"

        common = [
            sys.executable,
            "-m",
            "twampy",
        ]
        opts = [
            "--tos",
            str(args.tos),
            "--ttl",
            str(args.ttl),
            "--padding",
            str(args.padding),
        ]

        if args.mode == "responder":
            cmd = [
                *common,
                "responder-fleet",
                near,
                "--count",
                str(n),
                "--step",
                str(step),
                *opts,
                "-q",
            ]
        else:  # sender
            t_sub_base = args.target_port + offset * args.target_step
            far = f"{args.target_host}:{t_sub_base}"
            cmd = [
                *common,
                "sender-fleet",
                far,
                near,
                "--count",
                str(n),
                "--step",
                str(step),
                "--target-step",
                str(args.target_step),
                "--packets",
                str(args.packets),
                "--interval",
                str(args.interval),
                *opts,
                "-q",
            ]

        log.debug("spawn[%d]: %s", idx, " ".join(cmd))
        child = subprocess.Popen(cmd)
        children.append(child)
        print(f"  процесс {idx + 1}/{len(parts)} pid={child.pid}: {n} экз. с порта {sub_base}", flush=True)
        offset += n

    print("Все процессы запущены. Ctrl+C для остановки.", flush=True)

    try:
        for child in children:
            child.wait()
    except KeyboardInterrupt:
        print("Останавливаю дочерние процессы...", flush=True)
        for child in children:
            child.terminate()
        for child in children:
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                child.kill()
    print("spawn завершён.", flush=True)
