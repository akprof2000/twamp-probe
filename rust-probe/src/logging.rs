//! Журнал пробы: структурированные записи (уровень, компонент, поля «ключ=значение»)
//! в консоль и/или файл с ротацией по размеру и сжатием архивов (gzip своими руками).
//! Формат строки совпадает с журналом Go/C#-проб, чтобы все читались одинаково:
//!
//! 2026-07-24 09:48:23.123 INFO  [dispatcher] Пул воркеров запущен воркеров=256

use crate::config::LogConfig;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::sync::{Mutex, OnceLock};

/// Порог уровня журнала (Trace=0 … Error=4).
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Level {
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

impl Level {
    fn name(self) -> &'static str {
        match self {
            Level::Trace => "TRACE",
            Level::Debug => "DEBUG",
            Level::Info => "INFO ",
            Level::Warn => "WARN ",
            Level::Error => "ERROR",
        }
    }

    fn parse(name: &str) -> Level {
        match name.trim().to_ascii_lowercase().as_str() {
            "trace" => Level::Trace,
            "debug" => Level::Debug,
            "warn" | "warning" => Level::Warn,
            "error" | "fatal" => Level::Error,
            _ => Level::Info,
        }
    }
}

/// Единый приёмник журнала (консоль + файл с ротацией).
struct Sink {
    level: Level,
    console: bool,
    file: Option<RotatingFile>,
}

static SINK: OnceLock<Mutex<Sink>> = OnceLock::new();

/// Настраивает журнал по конфигурации. Вызывается один раз на старте.
pub fn setup(cfg: &LogConfig) -> Result<(), String> {
    let file = if !cfg.dir.is_empty() && !cfg.file_name.is_empty() {
        Some(RotatingFile::open(cfg)?)
    } else {
        None
    };
    let sink = Sink {
        level: Level::parse(&cfg.level),
        console: cfg.console,
        file,
    };
    let _ = SINK.set(Mutex::new(sink));
    Ok(())
}

/// Пишет одну запись журнала. `fields` — пары «ключ, значение».
pub fn log(component: &str, level: Level, message: &str, fields: &[(&str, String)]) {
    let Some(lock) = SINK.get() else {
        return; // журнал ещё не настроен — на старте ошибки идут в stderr напрямую
    };
    let mut sink = lock.lock().unwrap();
    if level < sink.level {
        return;
    }

    let ts = chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f");
    let mut line = format!("{ts} {} [{component}] {}", level.name(), fold(message));
    for (key, value) in fields {
        let v = fold(value);
        if v.contains([' ', '\t', '=']) {
            line.push_str(&format!(" {key}={v:?}"));
        } else {
            line.push_str(&format!(" {key}={v}"));
        }
    }
    line.push('\n');

    if sink.console {
        print!("{line}");
        let _ = std::io::stdout().flush();
    }
    if let Some(f) = sink.file.as_mut() {
        let _ = f.write(line.as_bytes());
    }
}

/// Сворачивает переносы строк в одну строку (многострочный вывод зонда не должен
/// ломать построчный разбор журнала).
fn fold(s: &str) -> String {
    s.trim().replace('\n', " ⏎ ")
}

/// Файл журнала с ротацией по размеру: при достижении предела текущий файл уходит
/// в архив с меткой времени (при compress — сжимается), старые архивы сверх лимита удаляются.
struct RotatingFile {
    path: std::path::PathBuf,
    max_size: u64,
    max_files: usize,
    compress: bool,
    file: File,
    size: u64,
}

impl RotatingFile {
    fn open(cfg: &LogConfig) -> Result<RotatingFile, String> {
        fs::create_dir_all(&cfg.dir).map_err(|e| format!("не удалось создать каталог журнала {}: {e}", cfg.dir))?;
        let path = std::path::Path::new(&cfg.dir).join(&cfg.file_name);
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .map_err(|e| format!("не удалось открыть файл журнала {}: {e}", path.display()))?;
        let size = file.metadata().map(|m| m.len()).unwrap_or(0);
        Ok(RotatingFile {
            path,
            max_size: (cfg.max_size_mb.max(1) as u64) * 1024 * 1024,
            max_files: cfg.max_files.max(0) as usize,
            compress: cfg.compress,
            file,
            size,
        })
    }

    fn write(&mut self, data: &[u8]) -> std::io::Result<()> {
        if self.max_size > 0 && self.size + data.len() as u64 > self.max_size {
            if let Err(e) = self.rotate() {
                eprintln!("ротация журнала не удалась: {e}");
            }
        }
        self.file.write_all(data)?;
        self.size += data.len() as u64;
        Ok(())
    }

    fn rotate(&mut self) -> std::io::Result<()> {
        let stamp = chrono::Local::now().format("%Y%m%d-%H%M%S");
        let archive = format!("{}.{stamp}", self.path.display());
        drop(std::mem::replace(&mut self.file, File::create(&self.path)?));
        fs::rename(&self.path, &archive)?;
        self.file = OpenOptions::new().create(true).append(true).open(&self.path)?;
        self.size = 0;
        if self.compress {
            if let Err(e) = gzip_file(&archive) {
                eprintln!("сжатие архива журнала не удалось: {e}");
            }
        }
        self.cleanup();
        Ok(())
    }

    /// Удаляет архивы сверх max_files, начиная с самых старых (имя содержит метку времени).
    fn cleanup(&self) {
        if self.max_files == 0 {
            return;
        }
        let prefix = format!("{}.", self.path.file_name().unwrap().to_string_lossy());
        let dir = self.path.parent().unwrap_or_else(|| std::path::Path::new("."));
        let mut archives: Vec<_> = fs::read_dir(dir)
            .into_iter()
            .flatten()
            .flatten()
            .map(|e| e.path())
            .filter(|p| {
                p.file_name()
                    .map(|n| n.to_string_lossy().starts_with(&prefix))
                    .unwrap_or(false)
            })
            .collect();
        if archives.len() <= self.max_files {
            return;
        }
        archives.sort();
        for old in &archives[..archives.len() - self.max_files] {
            let _ = fs::remove_file(old);
        }
    }
}

/// Сжимает файл в .gz (минимальный gzip: заголовок + deflate stored-блоки + CRC32).
/// Своя реализация, чтобы у пробы не было лишних зависимостей.
fn gzip_file(path: &str) -> std::io::Result<()> {
    let mut input = Vec::new();
    File::open(path)?.read_to_end(&mut input)?;
    let mut out = File::create(format!("{path}.gz"))?;

    // Заголовок gzip: magic, метод deflate, флаги, mtime=0, xfl, ОС=255.
    out.write_all(&[0x1f, 0x8b, 0x08, 0x00, 0, 0, 0, 0, 0x00, 0xff])?;
    // Тело: deflate без сжатия (stored-блоки) — совместимо с любым gunzip.
    for chunk in input.chunks(0xffff) {
        let last = chunk.len() < 0xffff || chunk.as_ptr() as usize + chunk.len() == input.as_ptr() as usize + input.len();
        out.write_all(&[if last { 1 } else { 0 }])?; // BFINAL для последнего блока
        let len = chunk.len() as u16;
        out.write_all(&len.to_le_bytes())?;
        out.write_all(&(!len).to_le_bytes())?;
        out.write_all(chunk)?;
    }
    out.write_all(&crc32(&input).to_le_bytes())?;
    out.write_all(&(input.len() as u32).to_le_bytes())?;
    fs::remove_file(path)
}

/// CRC32 (полином IEEE) — для gzip-хвоста.
fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xffff_ffffu32;
    for &byte in data {
        crc ^= byte as u32;
        for _ in 0..8 {
            let mask = (crc & 1).wrapping_neg();
            crc = (crc >> 1) ^ (0xedb8_8320 & mask);
        }
    }
    !crc
}

/// Компонент журнала: удобная обёртка, подставляющая своё имя в каждую запись.
#[derive(Clone, Copy)]
pub struct Logger(pub &'static str);

impl Logger {
    pub fn info(&self, msg: &str, fields: &[(&str, String)]) {
        log(self.0, Level::Info, msg, fields);
    }
    pub fn debug(&self, msg: &str, fields: &[(&str, String)]) {
        log(self.0, Level::Debug, msg, fields);
    }
    pub fn warn(&self, msg: &str, fields: &[(&str, String)]) {
        log(self.0, Level::Warn, msg, fields);
    }
    pub fn error(&self, msg: &str, fields: &[(&str, String)]) {
        log(self.0, Level::Error, msg, fields);
    }
}

/// Сокращение для поля журнала: `f("ключ", value)`.
pub fn f(key: &'static str, value: impl ToString) -> (&'static str, String) {
    (key, value.to_string())
}
