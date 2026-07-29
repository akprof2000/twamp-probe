//! Контракты обмена с сервером SPI.Twamp.Server — зеркало Go/C#-контрактов пробы.
//!
//! Тонкости совместимости (те же, что в Go-пробе):
//!   - сервер шлёт задачи (SetJobs) через Flurl/Newtonsoft: ключи camelCase,
//!     enum'ы бывают числом и строкой — поэтому [`TaskMode`]/[`TaskType`] принимают оба;
//!   - ответы пробы сериализуются camelCase, enum'ы строками;
//!   - даты Newtonsoft бывают со смещением и без — [`CsTime`] разбирает оба варианта.

use serde::de::{self, Deserializer, Visitor};
use serde::ser::Serializer;
use serde::{Deserialize, Serialize};
use std::fmt;

// --- Guid ---

/// Возвращает новый случайный UUID v4 в строковом виде (как Guid в C#).
pub fn new_guid() -> String {
    // Свой генератор без зависимостей: 16 случайных байт из наносекунд + счётчика.
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    let c = COUNTER.fetch_add(1, Ordering::Relaxed);
    let mut b = [0u8; 16];
    b[0..8].copy_from_slice(&nanos.to_le_bytes());
    b[8..16].copy_from_slice(&(c.wrapping_mul(0x9E3779B97F4A7C15)).to_le_bytes());
    b[6] = (b[6] & 0x0f) | 0x40; // версия 4
    b[8] = (b[8] & 0x3f) | 0x80; // вариант
    format!(
        "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]
    )
}

/// Аналог Guid.Empty.
pub const EMPTY_GUID: &str = "00000000-0000-0000-0000-000000000000";

// --- CsTime: дата в формате Newtonsoft ---

/// Дата в формате Newtonsoft: ISO 8601 со смещением и без, с дробными секундами и без.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CsTime(pub Option<chrono::DateTime<chrono::Local>>);

impl CsTime {
    /// Текущий момент.
    pub fn now() -> Self {
        CsTime(Some(chrono::Local::now()))
    }

    /// «Нулевая» дата (Go time.Time{} / C# DateTime.MinValue).
    pub fn zero() -> Self {
        CsTime(None)
    }

    /// Пустая ли дата.
    #[allow(dead_code)] // часть публичного контракта типа
    pub fn is_zero(&self) -> bool {
        self.0.is_none()
    }
}

impl Serialize for CsTime {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        match self.0 {
            // Пустое время — как C# DateTime.MinValue, сервер разберёт.
            None => s.serialize_str("0001-01-01T00:00:00"),
            // Дробные секунды + смещение — формат ISO 8601, который разбирает Newtonsoft.
            // Ровно 6 знаков (микросекунды): chrono поддерживает только %.3f/%.6f/%.9f,
            // а на неподдерживаемой точности его Display возвращает ошибку и to_string паникует.
            Some(dt) => s.serialize_str(&dt.format("%Y-%m-%dT%H:%M:%S%.6f%:z").to_string()),
        }
    }
}

impl<'de> Deserialize<'de> for CsTime {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct V;
        impl<'de> Visitor<'de> for V {
            type Value = CsTime;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("дата в формате ISO 8601")
            }
            fn visit_str<E: de::Error>(self, s: &str) -> Result<CsTime, E> {
                Ok(parse_cs_time(s))
            }
        }
        d.deserialize_str(V)
    }
}

/// Разбирает дату в любом из принятых форматов (без смещения — как локальную).
fn parse_cs_time(s: &str) -> CsTime {
    use chrono::{Local, NaiveDateTime, TimeZone};
    let s = s.trim();
    if s.is_empty() || s == "null" || s.starts_with("0001-01-01") {
        return CsTime(None);
    }
    // Со смещением (RFC3339).
    if let Ok(dt) = chrono::DateTime::parse_from_rfc3339(s) {
        return CsTime(Some(dt.with_timezone(&Local)));
    }
    // Без смещения, с дробными секундами и без — трактуем как локальное время.
    for fmt in ["%Y-%m-%dT%H:%M:%S%.f", "%Y-%m-%dT%H:%M:%S"] {
        if let Ok(naive) = NaiveDateTime::parse_from_str(s, fmt) {
            if let chrono::LocalResult::Single(dt) = Local.from_local_datetime(&naive) {
                return CsTime(Some(dt));
            }
        }
    }
    CsTime(None)
}

// --- TaskMode / TaskType: enum'ы, принимающие число и строку ---

/// Режим зондирования (WinPing / TWamp / TWampy).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskMode {
    WinPing,
    TWamp,
    TWampy,
}

impl TaskMode {
    /// Каноническое имя (как StringEnumConverter в C#).
    pub fn as_str(&self) -> &'static str {
        match self {
            TaskMode::WinPing => "WinPing",
            TaskMode::TWamp => "TWamp",
            TaskMode::TWampy => "TWampy",
        }
    }
}

impl Serialize for TaskMode {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for TaskMode {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct V;
        impl<'de> Visitor<'de> for V {
            type Value = TaskMode;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("режим числом (0/1/2) или строкой")
            }
            fn visit_u64<E: de::Error>(self, n: u64) -> Result<TaskMode, E> {
                match n {
                    0 => Ok(TaskMode::WinPing),
                    1 => Ok(TaskMode::TWamp),
                    2 => Ok(TaskMode::TWampy),
                    _ => Err(E::custom(format!("неизвестный номер режима {n}"))),
                }
            }
            fn visit_i64<E: de::Error>(self, n: i64) -> Result<TaskMode, E> {
                self.visit_u64(n as u64)
            }
            fn visit_str<E: de::Error>(self, s: &str) -> Result<TaskMode, E> {
                match s.to_ascii_lowercase().as_str() {
                    "winping" => Ok(TaskMode::WinPing),
                    "twamp" => Ok(TaskMode::TWamp),
                    "twampy" => Ok(TaskMode::TWampy),
                    _ => Err(E::custom(format!("неизвестный режим «{s}»"))),
                }
            }
        }
        d.deserialize_any(V)
    }
}

/// Тип задачи: разовая (Repeater) или по расписанию (Scheduler).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskType {
    Repeater,
    Scheduler,
}

impl Serialize for TaskType {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(match self {
            TaskType::Repeater => "Repeater",
            TaskType::Scheduler => "Scheduler",
        })
    }
}

impl<'de> Deserialize<'de> for TaskType {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct V;
        impl<'de> Visitor<'de> for V {
            type Value = TaskType;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("тип числом (0/1) или строкой")
            }
            fn visit_u64<E: de::Error>(self, n: u64) -> Result<TaskType, E> {
                match n {
                    0 => Ok(TaskType::Repeater),
                    1 => Ok(TaskType::Scheduler),
                    _ => Err(E::custom(format!("неизвестный номер типа задачи {n}"))),
                }
            }
            fn visit_i64<E: de::Error>(self, n: i64) -> Result<TaskType, E> {
                self.visit_u64(n as u64)
            }
            fn visit_str<E: de::Error>(self, s: &str) -> Result<TaskType, E> {
                match s.to_ascii_lowercase().as_str() {
                    "repeater" => Ok(TaskType::Repeater),
                    "scheduler" => Ok(TaskType::Scheduler),
                    _ => Err(E::custom(format!("неизвестный тип задачи «{s}»"))),
                }
            }
        }
        d.deserialize_any(V)
    }
}

// --- Контракты ---

/// Описание задачи зондирования, получаемое от сервера.
///
/// Совместимость с сервером требует двух послаблений (в Go они есть «из коробки»,
/// в Rust их нужно задать явно):
///   - **регистр имён**: задачи приходят через Flurl/Newtonsoft с ключами PascalCase
///     (`"Title"`, `"CronExpression"`), а веб-интерфейс шлёт camelCase — принимаем оба
///     варианта через `alias`;
///   - **необязательность полей**: сервер шлёт не весь набор (например, `ipAddress`
///     у него в задаче вообще нет), поэтому отсутствующие поля берут значения по
///     умолчанию, а не роняют разбор. Лишние поля (`DeletedAt`) serde игнорирует сам.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct TaskInfo {
    #[serde(alias = "IpAddress", alias = "IPAddress")]
    pub ip_address: String,
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Title")]
    pub title: String,
    #[serde(rename = "type", alias = "Type")]
    pub task_type: TaskType,
    #[serde(alias = "Mode")]
    pub mode: TaskMode,
    #[serde(alias = "CronExpression")]
    pub cron_expression: String,
    #[serde(alias = "CronWithSeconds")]
    pub cron_with_seconds: bool,
    #[serde(alias = "ContinueIfError")]
    pub continue_if_error: bool,
    #[serde(alias = "Repeats")]
    pub repeats: i64,
    #[serde(alias = "Circles")]
    pub circles: i64,
    #[serde(alias = "PauseSec")]
    pub pause_sec: u64,
    #[serde(alias = "TimeoutSec")]
    pub timeout_sec: i64,
    #[serde(alias = "Start")]
    pub start: CsTime,
    #[serde(alias = "End")]
    pub end: CsTime,
    #[serde(alias = "Create")]
    pub create: CsTime,
    #[serde(alias = "Delete")]
    pub delete: bool,
    #[serde(alias = "EndNode")]
    pub end_node: String,
    #[serde(alias = "Parameters")]
    pub parameters: std::collections::HashMap<String, String>,
    #[serde(alias = "RequestInfo")]
    pub request_info: String,
}

impl Default for TaskInfo {
    /// Значения по умолчанию совпадают с C#-контрактом сервера: задача по расписанию,
    /// режим TWamp, один повтор и один цикл.
    fn default() -> Self {
        TaskInfo {
            ip_address: String::new(),
            id: String::new(),
            title: String::new(),
            task_type: TaskType::Scheduler,
            mode: TaskMode::TWamp,
            cron_expression: String::new(),
            cron_with_seconds: false,
            continue_if_error: false,
            repeats: 1,
            circles: 1,
            pause_sec: 0,
            timeout_sec: 0,
            start: CsTime::zero(),
            end: CsTime::zero(),
            create: CsTime::zero(),
            delete: false,
            end_node: String::new(),
            parameters: std::collections::HashMap::new(),
            request_info: String::new(),
        }
    }
}

/// Результат одного замера зонда, передаваемый серверу.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActionData {
    pub result_id: String,
    pub creation: CsTime,
    pub task_id: String,
    pub end_node: String,
    #[serde(rename = "ipAddress")]
    pub ip_address: String,
    pub request_info: String,
    pub mode: String,
    pub call_line: String,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub exit_code: Option<i64>,
    pub outcome: String,
    pub console: String,
    pub error_console: String,
}

/// Идентификационные данные пробы (ответ на CheckIn).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Identify {
    pub ip_address: String,
    pub host_name: String,
    pub mac_address: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub title: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub description: String,
    pub request_info: String,
    pub version: String,
}

/// Пачка результатов с идентификатором для подтверждения (ACK).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultBatch {
    pub batch_id: String,
    pub items: Vec<ActionData>,
}

/// Исход запуска зонда (имена совпадают с C#/Go-enum).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RunOutcome {
    NotStarted,
    Running,
    Success,
    ExitCodeError,
    StartFailed,
    TimedOut,
}

impl RunOutcome {
    pub fn as_str(&self) -> &'static str {
        match self {
            RunOutcome::NotStarted => "NotStarted",
            RunOutcome::Running => "Running",
            RunOutcome::Success => "Success",
            RunOutcome::ExitCodeError => "ExitCodeError",
            RunOutcome::StartFailed => "StartFailed",
            RunOutcome::TimedOut => "TimedOut",
        }
    }
}

impl Serialize for RunOutcome {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(self.as_str())
    }
}

/// Состояние выполнения одной задачи (эндпоинт TaskStatus).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskRunInfo {
    pub task_id: String,
    pub title: String,
    pub mode: String,
    pub running: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_start: Option<CsTime>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_finish: Option<CsTime>,
    pub executions: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_run: Option<CsTime>,
    pub last_outcome: RunOutcome,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_exit_code: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_result: Option<String>,
    pub success_total: i64,
    pub error_total: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
}
