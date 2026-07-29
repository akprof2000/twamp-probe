//! Встроенный отправитель TWAMP-Light (эксперимент): порт режима «sender» из
//! nokia/twampy на Rust, работающий прямо в процессе пробы — без запуска внешнего
//! python. Формат тестовых пакетов, вычисление задержек/джиттера [RFC1889]/потерь
//! и текст итоговой таблицы воспроизведены так, чтобы серверный TwampyParser
//! разбирал вывод один-в-один с оригиналом (и со встроенными C#/Go-отправителями).

use std::net::{IpAddr, SocketAddr, ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const TIME_OFFSET: f64 = 2_208_988_800.0; // разница эпох NTP (1900) и Unix (1970), секунд
const ALL_BITS: f64 = 4_294_967_295.0; // маска 32-битной дробной части секунды NTP
const DEFAULT_FAR_PORT: u16 = 20001;

/// Текущее время в секундах Unix с высоким разрешением.
fn now() -> f64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0)
}

/// Разобранные параметры отправителя.
struct Options {
    remote: SocketAddr,
    local_port: u16,
    count: u32,
    interval_ms: u64,
    padmix: Vec<usize>,
}

/// Проводит замер встроенным отправителем: возвращает (таблица, текст ошибки).
/// Совместим по формату с python-выводом, поэтому серверный парсер работает без изменений.
pub fn run(args: &[String], deadline: Option<Instant>) -> (String, String) {
    let opts = match parse_args(args) {
        Ok(o) => o,
        Err(e) => return (String::new(), format!("Некорректные аргументы twampy sender: {e}")),
    };
    match session(&opts, deadline) {
        Ok(table) => (table, String::new()),
        Err(SessionError::Timeout) => (String::new(), "замер прерван по таймауту".to_string()),
        Err(SessionError::Io(e)) => (String::new(), format!("Ошибка встроенного twampy sender: {e}")),
    }
}

/// Ошибка сеанса: внешний таймаут задачи либо ошибка ввода-вывода.
enum SessionError {
    Timeout,
    Io(String),
}

/// Проводит один сеанс отправителя и возвращает текст таблицы.
fn session(opts: &Options, deadline: Option<Instant>) -> Result<String, SessionError> {
    let bind = if opts.remote.is_ipv6() {
        SocketAddr::from(([0u16, 0, 0, 0, 0, 0, 0, 0], opts.local_port))
    } else {
        SocketAddr::from(([0u8, 0, 0, 0], opts.local_port))
    };
    let socket = UdpSocket::bind(bind).map_err(|e| SessionError::Io(format!("не удалось открыть UDP-сокет: {e}")))?;

    let mut stats = Stats::default();
    let mut buf = [0u8; 9216];
    let interval = opts.interval_ms as f64 / 1000.0;
    let mut schedule = now();
    let end_time = schedule + opts.count as f64 * interval + 5.0;
    let mut idx: u32 = 0;
    let mut rng = Rng::new();

    loop {
        if let Some(d) = deadline {
            if Instant::now() >= d {
                return Err(SessionError::Timeout);
            }
        }

        // Забираем все уже пришедшие ответы (неблокирующе).
        socket.set_nonblocking(true).ok();
        loop {
            match socket.recv_from(&mut buf) {
                Ok((n, _)) if n >= 36 => {
                    if handle_reply(&buf[..n], &mut stats) + 1 == opts.count {
                        return Ok(stats.dump(idx)); // получены все ответы
                    }
                }
                _ => break,
            }
        }

        let send_time = now();
        if send_time >= schedule && idx < opts.count {
            schedule += interval;
            send_packet(&socket, opts, idx, send_time, &mut rng)
                .map_err(|e| SessionError::Io(format!("отправка пакета: {e}")))?;
            idx += 1;
        }

        if send_time > end_time {
            return Ok(stats.dump(idx));
        }

        // Единственная точка ожидания в цикле: спим на сокете до ближайшего
        // события — до следующего слота отправки, а когда всё отправлено, —
        // до истечения времени ожидания ответов. Без этого цикл крутился бы
        // вхолостую и занимал ядро почти целиком.
        let wake_at = if idx < opts.count { schedule } else { end_time };
        let wait = wake_at - now();
        if wait > 0.0 {
            socket.set_read_timeout(Some(Duration::from_secs_f64(wait))).ok();
            socket.set_nonblocking(false).ok();
            if let Ok((n, _)) = socket.recv_from(&mut buf) {
                if n >= 36 && handle_reply(&buf[..n], &mut stats) + 1 == opts.count {
                    return Ok(stats.dump(idx));
                }
            }
        }
    }
}

/// Разбирает один ответ рефлектора и добавляет его в статистику. Возвращает sseq.
fn handle_reply(data: &[u8], stats: &mut Stats) -> u32 {
    let t4 = now();
    let t3 = ntp_to_seconds(&data[4..12]); // время отправки рефлектором
    let t2 = ntp_to_seconds(&data[16..24]); // время приёма рефлектором
    let t1 = ntp_to_seconds(&data[28..36]); // наше время отправки (эхо)

    let delay_rt = (1000.0 * (t4 - t1 + t2 - t3)).max(0.0);
    let delay_ob = (1000.0 * (t2 - t1)).max(0.0);
    let delay_ib = (1000.0 * (t4 - t3)).max(0.0);

    let rseq = u32::from_be_bytes(data[0..4].try_into().unwrap());
    let sseq = u32::from_be_bytes(data[24..28].try_into().unwrap());
    stats.add(delay_rt, delay_ob, delay_ib, rseq, sseq);
    sseq
}

/// Отправляет один тестовый пакет (формат TWAMP-Light sender).
fn send_packet(socket: &UdpSocket, opts: &Options, seq: u32, t1: f64, rng: &mut Rng) -> std::io::Result<()> {
    // Заголовок 14 байт: seq(4) + NTP секунды(4) + NTP дробь(4) + оценка ошибки 0x3FFF(2).
    let padding = opts.padmix[rng.next() as usize % opts.padmix.len()];
    let mut packet = vec![0u8; 14 + padding];
    packet[0..4].copy_from_slice(&seq.to_be_bytes());
    packet[4..8].copy_from_slice(&((TIME_OFFSET + t1.floor()) as u32).to_be_bytes());
    packet[8..12].copy_from_slice(&(((t1 - t1.floor()) * ALL_BITS) as u32).to_be_bytes());
    packet[12..14].copy_from_slice(&0x3FFFu16.to_be_bytes());
    socket.send_to(&packet, opts.remote)?;
    Ok(())
}

/// Преобразует 8-байтную метку NTP (сек+дробь, big-endian) в секунды Unix.
fn ntp_to_seconds(ntp: &[u8]) -> f64 {
    let seconds = u32::from_be_bytes(ntp[0..4].try_into().unwrap());
    let fraction = u32::from_be_bytes(ntp[4..8].try_into().unwrap());
    seconds as f64 - TIME_OFFSET + fraction as f64 / ALL_BITS
}

/// Формат длительности из twampy: min/sec/ms/us по величине (для совместимости парсера).
pub fn format_duration(ms: f64) -> String {
    let abs = ms.abs();
    if abs > 60000.0 {
        format!("{:7.1}min", ms / 60000.0)
    } else if abs > 10000.0 {
        format!("{:7.1}sec", ms / 1000.0)
    } else if abs > 1000.0 {
        format!("{:7.2}sec", ms / 1000.0)
    } else if abs > 1.0 {
        format!("{:8.2}ms", ms)
    } else {
        format!("{:8}us", (ms * 1000.0) as i64)
    }
}

/// Накопитель статистики сеанса — точный порт TwampStatistics из twampy.
#[derive(Default)]
struct Stats {
    count: i64,
    min_ob: f64,
    min_ib: f64,
    min_rt: f64,
    max_ob: f64,
    max_ib: f64,
    max_rt: f64,
    sum_ob: f64,
    sum_ib: f64,
    sum_rt: f64,
    jitter_ob: f64,
    jitter_ib: f64,
    jitter_rt: f64,
    last_ob: f64,
    last_ib: f64,
    last_rt: f64,
    loss_ib: i64,
    loss_ob: i64,
}

impl Stats {
    /// Добавляет один ответ: задержки RT/OB/IB и последовательности рефлектора/отправителя.
    fn add(&mut self, delay_rt: f64, delay_ob: f64, delay_ib: f64, rseq: u32, sseq: u32) {
        if self.count == 0 {
            self.min_ob = delay_ob;
            self.max_ob = delay_ob;
            self.sum_ob = delay_ob;
            self.last_ob = delay_ob;
            self.min_ib = delay_ib;
            self.max_ib = delay_ib;
            self.sum_ib = delay_ib;
            self.last_ib = delay_ib;
            self.min_rt = delay_rt;
            self.max_rt = delay_rt;
            self.sum_rt = delay_rt;
            self.last_rt = delay_rt;
            self.loss_ib = rseq as i64;
            self.loss_ob = sseq as i64 - rseq as i64;
        } else {
            self.min_ob = self.min_ob.min(delay_ob);
            self.min_ib = self.min_ib.min(delay_ib);
            self.min_rt = self.min_rt.min(delay_rt);
            self.max_ob = self.max_ob.max(delay_ob);
            self.max_ib = self.max_ib.max(delay_ib);
            self.max_rt = self.max_rt.max(delay_rt);
            self.sum_ob += delay_ob;
            self.sum_ib += delay_ib;
            self.sum_rt += delay_rt;
            self.loss_ib = rseq as i64 - self.count;
            self.loss_ob = sseq as i64 - rseq as i64;

            if self.count == 1 {
                self.jitter_ob = (self.last_ob - delay_ob).abs();
                self.jitter_ib = (self.last_ib - delay_ib).abs();
                self.jitter_rt = (self.last_rt - delay_rt).abs();
            } else {
                self.jitter_ob += ((self.last_ob - delay_ob).abs() - self.jitter_ob) / 16.0;
                self.jitter_ib += ((self.last_ib - delay_ib).abs() - self.jitter_ib) / 16.0;
                self.jitter_rt += ((self.last_rt - delay_rt).abs() - self.jitter_rt) / 16.0;
            }
            self.last_ob = delay_ob;
            self.last_ib = delay_ib;
            self.last_rt = delay_rt;
        }
        self.count += 1;
    }

    /// Печатает таблицу направлений — тот же текст, что и twampy sender.
    fn dump(&self, total: u32) -> String {
        const BAR: &str = "===============================================================================";
        const DASH: &str = "-------------------------------------------------------------------------------";
        let mut s = String::new();
        s.push_str(BAR);
        s.push('\n');
        s.push_str("Direction         Min         Max         Avg          Jitter     Loss\n");
        s.push_str(DASH);
        s.push('\n');

        if self.count > 0 && total > 0 {
            let loss_rt = total as i64 - self.count;
            s.push_str(&format!("  Outbound:    {}\n", row(self.min_ob, self.max_ob, self.sum_ob / self.count as f64, self.jitter_ob, self.loss_ob, total)));
            s.push_str(&format!("  Inbound:     {}\n", row(self.min_ib, self.max_ib, self.sum_ib / self.count as f64, self.jitter_ib, self.loss_ib, total)));
            s.push_str(&format!("  Roundtrip:   {}\n", row(self.min_rt, self.max_rt, self.sum_rt / self.count as f64, self.jitter_rt, loss_rt, total)));
        } else {
            s.push_str("  NO STATS AVAILABLE (100% loss)\n");
        }

        s.push_str(DASH);
        s.push('\n');
        s.push_str("                                                    Jitter Algorithm [RFC1889]\n");
        s.push_str(BAR);
        s.push('\n');
        s
    }
}

/// Собирает одну строку направления: min/max/avg/jitter + процент потерь.
fn row(min: f64, max: f64, avg: f64, jitter: f64, loss: i64, total: u32) -> String {
    let loss_pct = 100.0 * loss as f64 / total as f64;
    format!(
        "{}  {}  {}  {}    {:5.1}%",
        format_duration(min),
        format_duration(max),
        format_duration(avg),
        format_duration(jitter),
        loss_pct
    )
}

/// Разбирает строку аргументов sender'а в параметры.
/// Раскладка: [-m twampy] sender <far> [<near>] [-c N] [-i мс] [--padding B] [--tos T] [--ttl H].
fn parse_args(tokens: &[String]) -> Result<Options, String> {
    let mut count = 100i64;
    let mut interval = 100i64;
    let mut padding = 0i64;
    let mut positionals: Vec<&str> = Vec::new();

    let mut i = 0;
    while i < tokens.len() {
        match tokens[i].as_str() {
            "-m" | "twampy" | "sender" => {}
            "-c" | "--count" => count = next_int(tokens, &mut i, count),
            "-i" | "--interval" => interval = next_int(tokens, &mut i, interval),
            "--padding" => padding = next_int(tokens, &mut i, padding),
            "--tos" | "--ttl" | "--dscp" => {
                let _ = next_int(tokens, &mut i, 0); // принимаем и игнорируем значение
            }
            "-d" | "-v" | "-q" | "--do-not-fragment" => {}
            other => {
                if !other.starts_with('-') {
                    positionals.push(other);
                }
            }
        }
        i += 1;
    }

    if positionals.is_empty() {
        return Err("не указан адрес рефлектора (far-end)".to_string());
    }

    let (far_host, far_port) = parse_addr(positionals[0], DEFAULT_FAR_PORT)?;
    let local_port = if positionals.len() > 1 {
        parse_addr(positionals[1], 0).map(|(_, p)| p).unwrap_or(0)
    } else {
        0
    };

    let remote = resolve(&far_host, far_port)?;
    let padmix: Vec<usize> = if padding > 0 {
        vec![padding as usize]
    } else if remote.is_ipv6() {
        vec![0, 0, 0, 0, 0, 0, 0, 514, 514, 514, 514, 1438]
    } else {
        vec![8, 8, 8, 8, 8, 8, 8, 534, 534, 534, 534, 1458]
    };

    Ok(Options {
        remote,
        local_port,
        count: count.clamp(1, 9999) as u32,
        interval_ms: interval.max(1) as u64,
        padmix,
    })
}

/// Читает целочисленное значение следующего токена, сдвигая индекс.
fn next_int(tokens: &[String], i: &mut usize, fallback: i64) -> i64 {
    if *i + 1 < tokens.len() {
        if let Ok(v) = tokens[*i + 1].parse::<i64>() {
            *i += 1;
            return v;
        }
    }
    fallback
}

/// Разбирает «ip:port» / «[ipv6]:port» / «ip» / «:port» в хост и порт.
fn parse_addr(addr: &str, default_port: u16) -> Result<(String, u16), String> {
    if addr.is_empty() || addr == ":0" {
        return Ok((String::new(), 0));
    }
    if let Some(rest) = addr.strip_prefix(':') {
        if let Ok(p) = rest.parse() {
            return Ok((String::new(), p)); // «:port» — только локальный порт
        }
    }
    if addr.contains("]:") {
        let idx = addr.rfind(':').unwrap();
        let host = addr[..idx].trim_matches(['[', ']']).to_string();
        let port = addr[idx + 1..].parse().map_err(|_| format!("неверный порт в «{addr}»"))?;
        return Ok((host, port));
    }
    if addr.contains(']') || addr.matches(':').count() > 1 {
        return Ok((addr.trim_matches(['[', ']']).to_string(), default_port)); // IPv6 без порта
    }
    if let Some((host, port)) = addr.split_once(':') {
        let port = port.parse().map_err(|_| format!("неверный порт в «{addr}»"))?;
        return Ok((host.to_string(), port));
    }
    Ok((addr.to_string(), default_port))
}

/// Преобразует адрес рефлектора в SocketAddr (с DNS-резолвингом по имени, IPv4 в приоритете).
fn resolve(host: &str, port: u16) -> Result<SocketAddr, String> {
    if host.is_empty() {
        return Ok(SocketAddr::from(([127, 0, 0, 1], port)));
    }
    if let Ok(ip) = host.parse::<IpAddr>() {
        return Ok(SocketAddr::new(ip, port));
    }
    let addrs: Vec<SocketAddr> = (host, port)
        .to_socket_addrs()
        .map_err(|e| format!("не удалось разрешить адрес «{host}»: {e}"))?
        .collect();
    addrs
        .iter()
        .find(|a| a.is_ipv4())
        .or_else(|| addrs.first())
        .copied()
        .ok_or_else(|| format!("адрес «{host}» не разрешён"))
}

/// Простейший ГПСЧ (xorshift) — для выбора паддинга, без внешних зависимостей.
struct Rng(u64);

impl Rng {
    fn new() -> Self {
        let seed = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos() as u64).unwrap_or(1);
        Rng(seed | 1)
    }
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::UdpSocket;
    use std::thread;

    /// Минимальный TWAMP-Light рефлектор: отражает пакет с метками t2/t3.
    fn start_reflector() -> (u16, std::sync::mpsc::Sender<()>) {
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = socket.local_addr().unwrap().port();
        let (tx, rx) = std::sync::mpsc::channel::<()>();
        socket.set_read_timeout(Some(Duration::from_millis(50))).unwrap();
        thread::spawn(move || {
            let mut buf = [0u8; 9216];
            let mut rseq: u32 = 0;
            loop {
                if rx.try_recv().is_ok() {
                    return;
                }
                if let Ok((n, addr)) = socket.recv_from(&mut buf) {
                    if n < 14 {
                        continue;
                    }
                    let t2 = now();
                    let sseq = u32::from_be_bytes(buf[0..4].try_into().unwrap());
                    let sender_t1: [u8; 8] = buf[4..12].try_into().unwrap();
                    let t3 = now();
                    let mut reply = [0u8; 36];
                    reply[0..4].copy_from_slice(&rseq.to_be_bytes());
                    rseq += 1;
                    write_ntp(&mut reply[4..12], t3);
                    write_ntp(&mut reply[16..24], t2);
                    reply[24..28].copy_from_slice(&sseq.to_be_bytes());
                    reply[28..36].copy_from_slice(&sender_t1);
                    let _ = socket.send_to(&reply, addr);
                }
            }
        });
        (port, tx)
    }

    fn write_ntp(target: &mut [u8], unix: f64) {
        target[0..4].copy_from_slice(&((TIME_OFFSET + unix.floor()) as u32).to_be_bytes());
        target[4..8].copy_from_slice(&(((unix - unix.floor()) * ALL_BITS) as u32).to_be_bytes());
    }

    fn args(s: &str) -> Vec<String> {
        s.split_whitespace().map(str::to_string).collect()
    }

    #[test]
    fn roundtrip_produces_table() {
        let (port, stop) = start_reflector();
        let (out, err) = run(&args(&format!("sender 127.0.0.1:{port} :0 -c 5 -i 20")), Some(Instant::now() + Duration::from_secs(5)));
        stop.send(()).ok();
        assert_eq!(err, "");
        assert!(out.contains("Direction"), "нет таблицы:\n{out}");
        assert!(out.contains("Roundtrip:"), "нет Roundtrip:\n{out}");
        assert!(out.contains("0.0%"), "ожидались нулевые потери:\n{out}");
    }

    #[test]
    fn no_reflector_full_loss() {
        let free = UdpSocket::bind("127.0.0.1:0").unwrap();
        let dead = free.local_addr().unwrap().port();
        drop(free);
        let (out, _) = run(&args(&format!("sender 127.0.0.1:{dead} :0 -c 3 -i 20")), Some(Instant::now() + Duration::from_secs(30)));
        assert!(out.contains("NO STATS AVAILABLE"), "ожидался NO STATS:\n{out}");
    }

    #[test]
    fn format_duration_units() {
        assert!(format_duration(0.4).ends_with("us"));
        assert!(format_duration(5.0).ends_with("ms"));
        assert!(format_duration(2500.0).ends_with("sec"));
        assert!(format_duration(120000.0).ends_with("min"));
    }

    #[test]
    fn parse_defaults_and_overrides() {
        let o = parse_args(&args("sender 10.0.0.5")).unwrap();
        assert_eq!(o.remote.port(), DEFAULT_FAR_PORT);
        assert_eq!(o.count, 100);
        assert_eq!(o.interval_ms, 100);

        let o = parse_args(&args("-m twampy sender 10.0.0.5:5000 :0 -c 10 -i 200 --padding 64")).unwrap();
        assert_eq!(o.remote.port(), 5000);
        assert_eq!(o.count, 10);
        assert_eq!(o.interval_ms, 200);
        assert_eq!(o.padmix, vec![64]);
    }
}
