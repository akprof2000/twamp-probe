//! Тесты контрактов и реестра задач: совместимость JSON с сервером и cron-планирование.

use crate::contracts::{ActionData, CsTime, TaskInfo, TaskMode, TaskType};
use crate::registry::TaskRegistry;
use crate::runregistry::RunRegistry;
use std::sync::Arc;

/// Типовая задача от сервера (как её шлёт SetJobs).
const TASK_JSON: &str = r#"[{"ipAddress":"127.0.0.1","id":"11111111-1111-1111-1111-111111111111",
"title":"проба","type":"Scheduler","mode":"TWampy","cronExpression":"*/1 * * * *",
"cronWithSeconds":false,"continueIfError":true,"repeats":1,"circles":1,"pauseSec":0,"timeoutSec":60,
"start":"2026-07-29T16:00:00","end":"2026-07-29T23:00:00","create":"2026-07-29T16:00:00",
"delete":false,"endNode":"127.0.0.1:20000","parameters":{"all":"-c 10"},"requestInfo":"http://s:9000"}]"#;

#[test]
fn task_json_from_server_is_parsed() {
    let jobs: Vec<TaskInfo> = serde_json::from_str(TASK_JSON).expect("разбор задач сервера");
    let task = &jobs[0];
    assert_eq!(task.mode, TaskMode::TWampy);
    assert_eq!(task.task_type, TaskType::Scheduler);
    assert_eq!(task.end_node, "127.0.0.1:20000");
    assert_eq!(task.timeout_sec, 60);
    assert!(task.end.0.is_some(), "дата окончания должна разобраться");
}

#[test]
fn enum_accepts_number_and_string() {
    // Flurl/Newtonsoft на сервере шлёт enum'ы и числом, и строкой — принимаем оба.
    let numeric = TASK_JSON.replace(r#""mode":"TWampy""#, r#""mode":2"#).replace(r#""type":"Scheduler""#, r#""type":1"#);
    let jobs: Vec<TaskInfo> = serde_json::from_str(&numeric).expect("разбор enum числом");
    assert_eq!(jobs[0].mode, TaskMode::TWampy);
    assert_eq!(jobs[0].task_type, TaskType::Scheduler);
}

#[test]
fn action_data_serializes_without_panic() {
    // Регрессия: неподдерживаемая точность дробных секунд (%.7f) роняла сериализацию,
    // потому что Display у chrono возвращает ошибку, а to_string на ней паникует.
    let action = ActionData {
        result_id: "r".into(),
        creation: CsTime::now(),
        task_id: "t".into(),
        end_node: "n".into(),
        ip_address: "127.0.0.1".into(),
        request_info: "http://s:9000".into(),
        mode: "TWampy".into(),
        call_line: "twampy(embedded) sender".into(),
        exit_code: Some(0),
        outcome: "Success".into(),
        console: "вывод".into(),
        error_console: String::new(),
    };
    let json = serde_json::to_string(&action).expect("сериализация результата");
    assert!(json.contains("\"creation\""), "нет поля creation: {json}");
    assert!(json.contains("\"resultId\":\"r\""), "ключи должны быть camelCase: {json}");
}

#[test]
fn merge_and_schedule_task() {
    let jobs: Vec<TaskInfo> = serde_json::from_str(TASK_JSON).unwrap();
    let runs = Arc::new(RunRegistry::new());
    let registry = TaskRegistry::new(Arc::clone(&runs));

    registry.merge_jobs(jobs);

    let ids = registry.known_task_ids();
    assert_eq!(ids.len(), 1, "задача должна попасть в реестр");
    assert_eq!(registry.all_tasks().len(), 1);

    // Планировщик обязан выставить момент следующего запуска.
    let status = runs.all();
    assert_eq!(status.len(), 1);
    assert!(status[0].next_run.is_some(), "не запланирован следующий запуск");

    // Очистка снимает все задачи (сторож связи).
    assert_eq!(registry.clear_all(), 1);
    assert!(registry.known_task_ids().is_empty());
}

#[test]
fn delete_flag_removes_task() {
    let jobs: Vec<TaskInfo> = serde_json::from_str(TASK_JSON).unwrap();
    let runs = Arc::new(RunRegistry::new());
    let registry = TaskRegistry::new(runs);
    registry.merge_jobs(jobs.clone());
    assert_eq!(registry.known_task_ids().len(), 1);

    let mut deleted = jobs;
    deleted[0].delete = true;
    registry.merge_jobs(deleted);
    assert!(registry.known_task_ids().is_empty(), "задача с delete должна быть удалена");
}

/// Тело SetJobs в том виде, в каком его реально шлёт сервер: ключи PascalCase
/// (Flurl/Newtonsoft по умолчанию), enum'ы числами, поля `ipAddress` нет вовсе,
/// зато есть лишнее `DeletedAt`.
const TASK_JSON_FROM_SERVER: &str = r#"[{"RequestInfo":"http://localhost:18445",
"Id":"33333333-3333-3333-3333-333333333333","Title":"уже-на-сервере","Type":1,"Mode":2,
"CronExpression":"*/5 * * * *","CronWithSeconds":false,"ContinueIfError":true,"Repeats":1,
"Circles":1,"PauseSec":0,"TimeoutSec":60,"Start":"2026-07-29T16:00:00Z","End":"2027-07-29T23:59:00Z",
"Create":"2026-07-29T16:00:00Z","Delete":false,"DeletedAt":null,"EndNode":"127.0.0.1:20000",
"Parameters":{"all":"-c 10 -i 100"}}]"#;

#[test]
fn task_json_in_server_format_is_parsed() {
    // Регрессия: раньше разбор падал с «missing field ipAddress» — serde требует
    // точного совпадения имён и наличия всех полей, тогда как сервер шлёт PascalCase
    // и не шлёт часть полей. Из-за этого проба не получала задачи вообще.
    let jobs: Vec<TaskInfo> = serde_json::from_str(TASK_JSON_FROM_SERVER).expect("разбор тела от сервера");
    let task = &jobs[0];
    assert_eq!(task.id, "33333333-3333-3333-3333-333333333333");
    assert_eq!(task.title, "уже-на-сервере");
    assert_eq!(task.task_type, TaskType::Scheduler, "Type=1 — задача по расписанию");
    assert_eq!(task.mode, TaskMode::TWampy, "Mode=2 — режим TWampy");
    assert_eq!(task.end_node, "127.0.0.1:20000");
    assert_eq!(task.timeout_sec, 60);
    assert_eq!(task.request_info, "http://localhost:18445");
    assert!(task.ip_address.is_empty(), "поля ipAddress сервер не шлёт — должно быть пустым");
    assert_eq!(task.parameters.get("all").map(String::as_str), Some("-c 10 -i 100"));
}

#[test]
fn task_in_server_format_gets_scheduled() {
    // Задача из настоящего тела сервера обязана попасть в реестр и получить план запуска.
    let jobs: Vec<TaskInfo> = serde_json::from_str(TASK_JSON_FROM_SERVER).unwrap();
    let runs = Arc::new(RunRegistry::new());
    let registry = TaskRegistry::new(Arc::clone(&runs));
    registry.merge_jobs(jobs);

    assert_eq!(registry.known_task_ids().len(), 1, "задача сервера должна попасть в реестр");
    assert!(runs.all()[0].next_run.is_some(), "не запланирован следующий запуск");
}
