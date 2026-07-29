//! Чтение конфигурации из appsettings.json — формат тот же, что у C#/Go-пробы,
//! поэтому Rust-вариант можно подложить в существующую инсталляцию.

use serde_json::Value;

/// Настройки одной измерительной утилиты (имя + аргументы по умолчанию).
#[derive(Clone, Debug)]
pub struct ToolConfig {
    pub name: String,
    pub default: String,
}

/// Настройки журнала (секция Logging).
#[derive(Clone, Debug)]
pub struct LogConfig {
    pub level: String,
    pub dir: String,
    pub file_name: String,
    pub max_size_mb: i64,
    pub max_files: i64,
    pub console: bool,
    pub compress: bool,
}

/// Настройки Rust-пробы (подмножество appsettings.json C#-пробы).
#[derive(Clone, Debug)]
pub struct Config {
    pub listen_addr: String,
    pub api_key: String,
    pub max_parallel: usize,
    pub max_pending_results: usize,
    pub persist_interval_sec: u64,
    pub server_timeout_hours: i64,
    pub log: LogConfig,
    pub ping: ToolConfig,
    pub twamp: ToolConfig,
    pub twampy: ToolConfig,
    pub twampy_embedded: bool,
}

impl Config {
    /// Читает appsettings.json рядом с исполняемым файлом.
    pub fn load(path: &str) -> Result<Config, String> {
        let raw = std::fs::read_to_string(path).map_err(|e| format!("не удалось прочитать {path}: {e}"))?;
        // Windows-файл может иметь UTF-8 BOM — serde_json его не переваривает.
        let raw = raw.trim_start_matches('\u{feff}');
        let root: Value =
            serde_json::from_str(raw).map_err(|e| format!("не удалось разобрать {path}: {e}"))?;

        Ok(Config {
            listen_addr: parse_urls(&s(&root, "Urls", "http://0.0.0.0:8443")),
            api_key: s(&root, "Auth:ApiKey", ""),
            max_parallel: resolve_parallel(n(&root, "Probe:MaxParallel", 0)),
            max_pending_results: n(&root, "Probe:MaxPendingResults", 100_000) as usize,
            persist_interval_sec: n(&root, "Probe:PersistIntervalSec", 5) as u64,
            server_timeout_hours: n(&root, "Probe:ServerTimeoutHours", 24),
            log: LogConfig {
                level: s(&root, "Logging:Level", "Info"),
                dir: s(&root, "Logging:Dir", "log"),
                file_name: s(&root, "Logging:FileName", "probe.log"),
                max_size_mb: n(&root, "Logging:MaxSizeMb", 10),
                max_files: n(&root, "Logging:MaxFiles", 20),
                console: b(&root, "Logging:Console", true),
                compress: b(&root, "Logging:Compress", true),
            },
            ping: ToolConfig {
                name: s(&root, "ping:name", "ping"),
                default: s(&root, "ping:default", ""),
            },
            twamp: ToolConfig {
                name: s(&root, "twamp:name", "./twping"),
                default: s(&root, "twamp:default", ""),
            },
            twampy: ToolConfig {
                name: s(&root, "twampy:name", "python3"),
                default: s(&root, "twampy:default", ""),
            },
            twampy_embedded: b(&root, "twampy:embedded", false),
        })
    }
}

/// Число воркеров: явное значение (>0) — как есть; 0 — автоподбор «ядра × 16»
/// с потолком 10000 и полом 16. Зонды в основном ждут I/O, поэтому воркеров нужно много.
fn resolve_parallel(configured: i64) -> usize {
    if configured > 0 {
        return configured as usize;
    }
    let cpus = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    (cpus * 16).clamp(16, 10_000)
}

/// Выделяет адрес прослушивания из строки Urls ASP.NET ("http://0.0.0.0:8443").
fn parse_urls(urls: &str) -> String {
    let first = urls.split(';').next().unwrap_or(urls);
    first
        .trim_start_matches("http://")
        .trim_start_matches("https://")
        .trim_end_matches('/')
        .to_string()
}

/// Спускается по вложенным объектам JSON по пути «Секция:Ключ».
fn dig<'a>(root: &'a Value, path: &str) -> Option<&'a Value> {
    let mut cur = root;
    for part in path.split(':') {
        cur = cur.get(part)?;
    }
    Some(cur)
}

/// Строка по пути (или значение по умолчанию).
fn s(root: &Value, path: &str, def: &str) -> String {
    dig(root, path).and_then(|v| v.as_str()).map(str::to_string).unwrap_or_else(|| def.to_string())
}

/// Целое по пути (число или строка с числом).
fn n(root: &Value, path: &str, def: i64) -> i64 {
    match dig(root, path) {
        Some(Value::Number(num)) => num.as_i64().unwrap_or(def),
        Some(Value::String(str_val)) => str_val.parse().unwrap_or(def),
        _ => def,
    }
}

/// Логическое по пути (true/false или строка).
fn b(root: &Value, path: &str, def: bool) -> bool {
    match dig(root, path) {
        Some(Value::Bool(v)) => *v,
        Some(Value::String(str_val)) => str_val.parse().unwrap_or(def),
        _ => def,
    }
}
