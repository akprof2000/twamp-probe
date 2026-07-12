#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2013-2026 Nokia

"""Общие примитивы TWAMP/TWAMP-light.

Модуль вынесен из исходного twampy.py, чтобы одни и те же хелперы (метки
времени NTP, разбор адресов, отбивка/паддинг) использовались как классическими
одиночными режимами (twampy.__main__), так и масштабируемым флотом
(twampy.fleet). Логика форматов пакетов не менялась — только вынесена.
"""

import logging
import struct
import sys
import time

# На Windows time.time() имеет низкое разрешение — используем perf_counter
# c однократной привязкой к абсолютному времени.
if sys.platform == "win32":
    time0 = time.time() - time.perf_counter()

# Константы для конвертации между python-временем и 8-байтным NTP [RFC1305]
TIMEOFFSET = 2208988800  # Разница: 1-JAN-1900 → 1-JAN-1970
ALLBITS = 0xFFFFFFFF  # Маска для 32-битной дробной части секунды

# Общий логгер пакета — конфигурируется в __main__.main()
log = logging.getLogger("twampy")


def now():
    """Текущее время в секундах (на Windows — с высоким разрешением)."""
    if sys.platform == "win32":
        return time.perf_counter() + time0
    return time.time()


def time_ntp2py(data):
    """Преобразовать 8-байтный NTP [RFC1305] в python-timestamp."""
    ta, tb = struct.unpack("!2I", data)
    t = ta - TIMEOFFSET + float(tb) / float(ALLBITS)
    return t


def zeros(nbr):
    """Вернуть nbr нулевых байт (паддинг тестового пакета)."""
    return struct.pack(f"!{nbr}B", *[0 for _ in range(nbr)])


def dp(ms):
    """Формат длительности: минуты/секунды/мс/мкс в зависимости от величины."""
    if abs(ms) > 60000:
        return f"{float(ms / 60000):7.1f}min"
    if abs(ms) > 10000:
        return f"{float(ms / 1000):7.1f}sec"
    if abs(ms) > 1000:
        return f"{float(ms / 1000):7.2f}sec"
    if abs(ms) > 1:
        return f"{ms:8.2f}ms"
    return f"{int(ms * 1000):8d}us"


def parse_addr(addr, default_port=20000):
    """Разобрать строку адреса в (ip, port, ip_version).

    Поддерживает IPv4/IPv6 с портом и без. Пустая строка → localhost.
    """
    if addr == "":
        # адрес не задан (по умолчанию: localhost IPv4 или IPv6)
        return "", default_port, 0
    elif "]:" in addr:
        # IPv6-адрес с портом
        ip, port = addr.rsplit(":", 1)
        return ip.strip("[]"), int(port), 6
    elif "]" in addr:
        # IPv6-адрес без порта
        return addr.strip("[]"), default_port, 6
    elif addr.count(":") > 1:
        # IPv6-адрес без порта
        return addr, default_port, 6
    elif ":" in addr:
        # IPv4-адрес с портом
        ip, port = addr.split(":")
        return ip, int(port), 4
    else:
        # IPv4-адрес без порта
        return addr, default_port, 4
