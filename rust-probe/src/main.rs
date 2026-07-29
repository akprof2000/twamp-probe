//! SPI TWamp Probe (Rust) — экспериментальный порт пробы на Rust.
//!
//! Полностью совместим с сервером SPI.Twamp.Server: те же эндпоинты
//! api/probeinterface (CheckIn, SetJobs, TaskIds, Tasks, TaskStatus, CheckData,
//! ConfirmData), тот же контракт JSON, та же механика ACK-доставки результатов,
//! инкрементального слияния задач и cron-планирования. Конфигурация — тот же
//! appsettings.json. Один статический бинарник; снаружи нужны только измерительные
//! утилиты (twping / python3+twampy / ping), а режим TWampy умеет работать
//! встроенным отправителем вообще без внешних процессов.

mod config;
mod contracts;
mod dispatcher;
mod logging;
mod registry;
mod resultstore;
mod runner;
mod runregistry;
mod twampysender;
mod watchdog;
#[cfg(test)]
mod probe_test;

use config::Config;
use contracts::{Identify, RunOutcome, TaskInfo, TaskRunInfo};
use dispatcher::Dispatcher;
use logging::{f, Logger};
use registry::TaskRegistry;
use resultstore::ResultStore;
use runner::ProbeRunner;
use runregistry::RunRegistry;
use std::sync::Arc;
use std::time::Duration;
use tiny_http::{Header, Method, Request, Response, Server};
use watchdog::ContactTracker;

/// Версия отображается в списке проб на сервере (поле Version в CheckIn).
/// Релизные сборки прошивают версию тега через переменную окружения PROBE_VERSION.
const PROBE_VERSION: &str = match option_env!("PROBE_VERSION") {
    Some(v) => v,
    None => "0.0.0-rust-dev",
};

static LOG_MAIN: Logger = Logger("main");
static LOG_HTTP: Logger = Logger("http");

/// Общее состояние HTTP-обработчиков.
struct AppState {
    cfg: Arc<Config>,
    results: Arc<ResultStore>,
    tasks: Arc<TaskRegistry>,
    runs: Arc<RunRegistry>,
    tracker: Arc<ContactTracker>,
}

fn main() {
    // Конфигурация читается до настройки журнала (в ней его параметры),
    // поэтому об ошибке на этом шаге сообщаем напрямую в stderr.
    let cfg = match Config::load("appsettings.json") {
        Ok(c) => Arc::new(c),
        Err(e) => {
            eprintln!("Ошибка конфигурации: {e}");
            std::process::exit(1);
        }
    };
    if let Err(e) = logging::setup(&cfg.log) {
        eprintln!("Не удалось настроить журнал: {e}");
        std::process::exit(1);
    }

    LOG_MAIN.info(
        "Проба запускается",
        &[
            f("версия", PROBE_VERSION),
            f("адрес", cfg.listen_addr.clone()),
            f("воркеров", cfg.max_parallel),
            f("уровень_журнала", cfg.log.level.clone()),
            f("встроенный_twampy", cfg.twampy_embedded),
        ],
    );

    // Сборка компонентов: хранилище результатов → раннер → диспетчер → реестр задач.
    let results = Arc::new(ResultStore::new(cfg.max_pending_results));
    results.load();
    let runs = Arc::new(RunRegistry::new());
    let runner = Arc::new(ProbeRunner::new(Arc::clone(&cfg), Arc::clone(&results), Arc::clone(&runs)));
    let dispatcher = Dispatcher::start(cfg.max_parallel, runner);
    let tasks = TaskRegistry::new(Arc::clone(&runs));
    tasks.start(Arc::clone(&dispatcher));
    tasks.load();

    // Периодический снимок недоставленных результатов на диск.
    {
        let results = Arc::clone(&results);
        let period = Duration::from_secs(cfg.persist_interval_sec.max(1));
        std::thread::spawn(move || loop {
            std::thread::sleep(period);
            results.flush();
        });
    }

    // Сторож связи: молчание сервера дольше порога означает, что пробу удалили.
    let tracker = Arc::new(ContactTracker::new());
    watchdog::start(cfg.server_timeout_hours, Arc::clone(&tracker), Arc::clone(&tasks), Arc::clone(&results));

    let state = Arc::new(AppState {
        cfg: Arc::clone(&cfg),
        results: Arc::clone(&results),
        tasks: Arc::clone(&tasks),
        runs: Arc::clone(&runs),
        tracker: Arc::clone(&tracker),
    });

    let server = match Server::http(&cfg.listen_addr) {
        Ok(s) => s,
        Err(e) => {
            LOG_HTTP.error("Не удалось открыть HTTP-порт", &[f("адрес", cfg.listen_addr.clone()), f("ошибка", e)]);
            std::process::exit(1);
        }
    };
    LOG_HTTP.info(
        "HTTP-сервер слушает",
        &[f("адрес", cfg.listen_addr.clone()), f("аутентификация", !cfg.api_key.is_empty())],
    );

    // Пул обработчиков запросов: длинный опрос CheckData занимает поток надолго,
    // поэтому обработчиков заметно больше, чем ядер.
    let server = Arc::new(server);
    let workers = (std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4) * 4).max(16);
    let mut handles = Vec::new();
    for _ in 0..workers {
        let server = Arc::clone(&server);
        let state = Arc::clone(&state);
        handles.push(std::thread::spawn(move || {
            for request in server.incoming_requests() {
                handle(&state, request);
            }
        }));
    }
    for h in handles {
        let _ = h.join();
    }
}

/// Разбирает запрос и вызывает нужный обработчик.
/// ASP.NET сопоставляет пути и query-параметры регистронезависимо — повторяем это.
fn handle(state: &Arc<AppState>, mut request: Request) {
    let url = request.url().to_string();
    let path = url.split('?').next().unwrap_or("").to_ascii_lowercase();
    let query = url.split_once('?').map(|(_, q)| q.to_string()).unwrap_or_default();
    let method = request.method().clone();

    // Проверка ключа API (если включён).
    if !state.cfg.api_key.is_empty() {
        let provided = request
            .headers()
            .iter()
            .find(|h| h.field.equiv("X-Api-Key"))
            .map(|h| h.value.as_str().to_string())
            .unwrap_or_default();
        if provided != state.cfg.api_key {
            let _ = request.respond(Response::from_string("Неверный ключ API").with_status_code(401));
            return;
        }
    }

    // Любое обращение сервера сбрасывает отсчёт сторожа связи.
    state.tracker.mark();

    let response = match (&method, path.as_str()) {
        (Method::Post, "/api/probeinterface/checkin") => check_in(state, &query),
        (Method::Post, "/api/probeinterface/setjobs") => set_jobs(state, &mut request),
        (Method::Get, "/api/probeinterface/taskids") => json(&state.tasks.known_task_ids()),
        (Method::Get, "/api/probeinterface/tasks") => json(&state.tasks.all_tasks()),
        (Method::Get, "/api/probeinterface/taskstatus") => task_status(state, &query),
        (Method::Get, "/api/probeinterface/checkdata") => check_data(state),
        (Method::Post, "/api/probeinterface/confirmdata") => confirm_data(state, &query),
        _ => Response::from_string("Не найдено").with_status_code(404),
    };
    let _ = request.respond(response);
}

/// Сериализует ответ в JSON (camelCase — как AddNewtonsoftJson у C#-пробы).
fn json<T: serde::Serialize>(value: &T) -> Response<std::io::Cursor<Vec<u8>>> {
    let body = serde_json::to_vec(value).unwrap_or_else(|_| b"null".to_vec());
    let header = Header::from_bytes(&b"Content-Type"[..], &b"application/json; charset=utf-8"[..]).unwrap();
    Response::from_data(body).with_header(header)
}

/// Читает параметр строки запроса без учёта регистра имени.
fn query_param(query: &str, name: &str) -> String {
    for pair in query.split('&') {
        if let Some((key, value)) = pair.split_once('=') {
            if key.eq_ignore_ascii_case(name) {
                return url_decode(value);
            }
        }
    }
    String::new()
}

/// Минимальное URL-декодирование (%XX и «+»).
fn url_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'%' if i + 2 < bytes.len() => {
                if let Ok(b) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                    out.push(b);
                    i += 3;
                    continue;
                }
                out.push(bytes[i]);
                i += 1;
            }
            b'+' => {
                out.push(b' ');
                i += 1;
            }
            b => {
                out.push(b);
                i += 1;
            }
        }
    }
    String::from_utf8_lossy(&out).to_string()
}

/// Возвращает паспорт пробы: адреса, имя хоста, версию.
fn check_in(state: &Arc<AppState>, query: &str) -> Response<std::io::Cursor<Vec<u8>>> {
    let request_info = query_param(query, "requestInfo");
    LOG_HTTP.info("CheckIn от сервера", &[f("сервер", request_info.clone()), f("версия_пробы", PROBE_VERSION)]);
    let _ = state;

    let host = hostname();
    json(&Identify {
        ip_address: local_ip(),
        host_name: host,
        mac_address: "00:00:00:00:00:00".to_string(),
        title: String::new(),
        description: String::new(),
        request_info,
        version: PROBE_VERSION.to_string(),
    })
}

/// Принимает изменившиеся задачи и сливает их в реестр.
fn set_jobs(state: &Arc<AppState>, request: &mut Request) -> Response<std::io::Cursor<Vec<u8>>> {
    let mut body = String::new();
    if std::io::Read::read_to_string(request.as_reader(), &mut body).is_err() {
        return Response::from_string("Не удалось прочитать тело запроса").with_status_code(400);
    }
    let jobs: Vec<TaskInfo> = match serde_json::from_str(&body) {
        Ok(v) => v,
        Err(e) => return Response::from_string(format!("Некорректное тело запроса: {e}")).with_status_code(400),
    };
    LOG_HTTP.info("SetJobs: получены изменения задач", &[f("задач", jobs.len())]);
    state.tasks.merge_jobs(jobs);
    Response::from_data(Vec::new())
}

/// Возвращает состояние выполнения задач с фильтрами и пагинацией.
fn task_status(state: &Arc<AppState>, query: &str) -> Response<std::io::Cursor<Vec<u8>>> {
    let skip: usize = query_param(query, "skip").parse().unwrap_or(0);
    let take: usize = query_param(query, "take").parse().unwrap_or(100).clamp(1, 500);
    let title = query_param(query, "title").to_lowercase();
    let outcome = query_param(query, "outcome");

    let mut items: Vec<TaskRunInfo> = state
        .runs
        .all()
        .into_iter()
        .filter(|t| title.is_empty() || t.title.to_lowercase().contains(&title))
        .filter(|t| {
            if outcome.is_empty() {
                return true;
            }
            let running = outcome.eq_ignore_ascii_case("Running") && t.running > 0;
            running || t.last_outcome.as_str().eq_ignore_ascii_case(&outcome)
        })
        .collect();

    // Сначала выполняющиеся и проблемные, затем по названию — как у C#/Go-проб.
    let bad = |o: RunOutcome| {
        matches!(o, RunOutcome::ExitCodeError | RunOutcome::StartFailed | RunOutcome::TimedOut) as i32
    };
    items.sort_by(|x, y| {
        y.running
            .cmp(&x.running)
            .then_with(|| bad(y.last_outcome).cmp(&bad(x.last_outcome)))
            .then_with(|| x.title.to_lowercase().cmp(&y.title.to_lowercase()))
    });

    let total = items.len();
    let start = skip.min(total);
    let end = (start + take).min(total);
    json(&serde_json::json!({ "total": total, "items": &items[start..end] }))
}

/// Выдаёт пачку результатов длинным опросом (до 30 секунд).
fn check_data(state: &Arc<AppState>) -> Response<std::io::Cursor<Vec<u8>>> {
    json(&state.results.take_batch(Duration::from_secs(30)))
}

/// Подтверждает запись пачки сервером — проба удаляет её.
fn confirm_data(state: &Arc<AppState>, query: &str) -> Response<std::io::Cursor<Vec<u8>>> {
    let batch_id = query_param(query, "batchId").to_lowercase();
    json(&state.results.confirm(&batch_id))
}

/// Имя хоста (через переменные окружения — без внешних зависимостей).
fn hostname() -> String {
    std::env::var("COMPUTERNAME")
        .or_else(|_| std::env::var("HOSTNAME"))
        .unwrap_or_else(|_| "unknown".to_string())
}

/// Локальный IP-адрес: определяется через «подключение» UDP-сокета к внешнему адресу
/// (пакеты не отправляются — ОС лишь выбирает исходящий интерфейс).
fn local_ip() -> String {
    std::net::UdpSocket::bind("0.0.0.0:0")
        .and_then(|s| {
            s.connect("8.8.8.8:80")?;
            s.local_addr()
        })
        .map(|a| a.ip().to_string())
        .unwrap_or_else(|_| "0.0.0.0".to_string())
}
