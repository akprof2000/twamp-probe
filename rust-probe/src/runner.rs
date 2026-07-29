//! Выполнение зонда для задачи — аналог Go ProbeRunner / C# ProbeRunner.
//! Для каждого узла задачи проходит Circles × Repeats запусков; собирает результат
//! (вывод, ошибки, код выхода, исход) и кладёт его в хранилище результатов.
//! Режим TWampy при twampy:embedded выполняется встроенным отправителем без процесса.

use crate::config::Config;
use crate::contracts::{new_guid, ActionData, CsTime, RunOutcome, TaskInfo, TaskMode};
use crate::logging::{f, Logger};
use crate::resultstore::ResultStore;
use crate::runregistry::RunRegistry;
use crate::twampysender;
use std::io::Read;
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::time::{Duration, Instant};

static LOG: Logger = Logger("runner");

/// Раннер зондов: держит конфигурацию, хранилище результатов и реестр статусов.
pub struct ProbeRunner {
    cfg: Arc<Config>,
    results: Arc<ResultStore>,
    registry: Arc<RunRegistry>,
    base_dir: String,
}

impl ProbeRunner {
    pub fn new(cfg: Arc<Config>, results: Arc<ResultStore>, registry: Arc<RunRegistry>) -> Self {
        let base_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_string_lossy().to_string()))
            .unwrap_or_else(|| ".".to_string());
        ProbeRunner { cfg, results, registry, base_dir }
    }

    /// Выполняет задачу для всех её узлов (узлы обрабатываются последовательно).
    pub fn run_for_nodes(&self, task: &TaskInfo) {
        self.registry.mark_started(task);
        for node in split_nodes(&task.end_node) {
            self.run_single_node(task, &node);
        }
        self.registry.mark_finished(&task.id);
    }

    /// Проходит Circles × Repeats запусков зонда для одного узла.
    fn run_single_node(&self, task: &TaskInfo, node: &str) {
        let (exec, args, env) = self.build_command(task, node);
        let circles = task.circles.max(1);
        let repeats = task.repeats.max(1);

        for circle in 0..circles {
            for _ in 0..repeats {
                let result = if task.mode == TaskMode::TWampy && self.cfg.twampy_embedded {
                    self.execute_embedded_twampy(task, node, &args)
                } else {
                    self.execute_once(task, node, &exec, &args, &env)
                };
                self.results.add(result);
            }
            // Пауза между циклами (кроме последнего).
            if circle != circles - 1 && task.pause_sec > 0 {
                std::thread::sleep(Duration::from_secs(task.pause_sec));
            }
        }
    }

    /// Формирует (исполняемый файл, аргументы, переменные окружения) по режиму задачи.
    fn build_command(&self, task: &TaskInfo, node: &str) -> (String, Vec<String>, Vec<(String, String)>) {
        let params = self.task_parameters(task);
        match task.mode {
            TaskMode::WinPing => {
                // Для ping адрес узла идёт первым аргументом, затем параметры.
                let mut args = vec![node.to_string()];
                args.extend(or_default(params, &self.cfg.ping.default));
                (self.cfg.ping.name.clone(), args, Vec::new())
            }
            TaskMode::TWampy => {
                // «python -m twampy sender <far-end> :0 [опции]» — узел первым, порт эфемерный.
                let mut args = vec!["-m".into(), "twampy".into(), "sender".into(), node.to_string(), ":0".into()];
                args.extend(or_default(params, &self.cfg.twampy.default));
                let pythonpath = std::env::var("PYTHONPATH").unwrap_or_default();
                let sep = if cfg!(windows) { ";" } else { ":" };
                let env = vec![("PYTHONPATH".to_string(), format!("{}{sep}{pythonpath}", self.base_dir))];
                (self.cfg.twampy.name.clone(), args, env)
            }
            TaskMode::TWamp => {
                // Для twping сначала параметры, адрес узла — последним.
                let mut args = or_default(params, &self.cfg.twamp.default);
                args.push(node.to_string());
                (self.cfg.twamp.name.clone(), args, Vec::new())
            }
        }
    }

    /// Собирает аргументы из параметров задачи (значения через пробел).
    fn task_parameters(&self, task: &TaskInfo) -> Vec<String> {
        let mut args = Vec::new();
        for value in task.parameters.values() {
            args.extend(value.split_whitespace().map(str::to_string));
        }
        args
    }

    /// Запускает внешний процесс зонда один раз и возвращает собранный результат.
    fn execute_once(&self, task: &TaskInfo, node: &str, exec: &str, args: &[String], env: &[(String, String)]) -> ActionData {
        let call_line = format!("{exec} {}", args.join(" "));
        let started = Instant::now();
        let timeout = if task.timeout_sec > 0 { Some(Duration::from_secs(task.timeout_sec as u64)) } else { None };

        let mut command = Command::new(exec);
        command.args(args).stdout(Stdio::piped()).stderr(Stdio::piped()).stdin(Stdio::null());
        for (key, value) in env {
            command.env(key, value);
        }

        let mut child = match command.spawn() {
            Ok(c) => c,
            Err(e) => {
                // Зонд не запустился (например, утилита не установлена) — ошибка обязана
                // дойти до сервера как результат, иначе задача выглядит «молча пропавшей».
                let message = format!("Не удалось запустить зонд «{exec}»: {e}");
                LOG.error("Задача: зонд не запустился", &[f("название", task.title.clone()), f("узел", node), f("режим", task.mode.as_str()), f("команда", call_line.clone()), f("ошибка", e), f("задача", task.id.clone())]);
                self.registry.report_outcome(&task.id, RunOutcome::StartFailed, None, &message);
                return self.build_result(task, node, call_line, None, RunOutcome::StartFailed, String::new(), message);
            }
        };

        let (output, error, exit_code, timed_out) = wait_with_timeout(&mut child, timeout);
        let mut error = error;

        if timed_out {
            let note = format!("Задача прервана по таймауту {} c и принудительно завершена.", task.timeout_sec);
            error = join_non_empty(&error, &note);
        } else if exit_code != 0 {
            let note = format!("Процесс зонда завершился с кодом {exit_code}.");
            error = join_non_empty(&error, &note);
        }

        let outcome = if timed_out {
            RunOutcome::TimedOut
        } else if exit_code != 0 {
            RunOutcome::ExitCodeError
        } else {
            RunOutcome::Success
        };
        let summary = if outcome == RunOutcome::Success { last_line(&output) } else { error.clone() };
        self.registry.report_outcome(&task.id, outcome, Some(exit_code as i64), &summary);
        log_run(task, node, outcome, exit_code as i64, started.elapsed(), &summary);

        self.build_result(task, node, call_line, Some(exit_code as i64), outcome, output, error)
    }

    /// Выполняет замер встроенным отправителем twampy (эксперимент): без внешнего процесса.
    fn execute_embedded_twampy(&self, task: &TaskInfo, node: &str, args: &[String]) -> ActionData {
        let call_line = format!("twampy(embedded) {}", args.join(" "));
        let started = Instant::now();
        let deadline = if task.timeout_sec > 0 { Some(started + Duration::from_secs(task.timeout_sec as u64)) } else { None };

        let (output, mut err_text) = twampysender::run(args, deadline);
        let timed_out = err_text.contains("таймаут") && output.is_empty();
        if timed_out {
            err_text = format!("Задача прервана по таймауту {} c.", task.timeout_sec);
        }

        let outcome = if timed_out {
            RunOutcome::TimedOut
        } else if !err_text.is_empty() {
            RunOutcome::ExitCodeError
        } else {
            RunOutcome::Success
        };
        let exit_code = if timed_out { None } else { Some(0) };
        let summary = if outcome == RunOutcome::Success { last_line(&output) } else { err_text.clone() };
        self.registry.report_outcome(&task.id, outcome, exit_code, &summary);
        log_run(task, node, outcome, exit_code.unwrap_or(0), started.elapsed(), &summary);

        self.build_result(task, node, call_line, exit_code, outcome, output, err_text)
    }

    /// Собирает ActionData для отправки серверу.
    #[allow(clippy::too_many_arguments)]
    fn build_result(&self, task: &TaskInfo, node: &str, call_line: String, exit_code: Option<i64>, outcome: RunOutcome, console: String, error_console: String) -> ActionData {
        ActionData {
            result_id: new_guid(),
            creation: CsTime::now(),
            task_id: task.id.clone(),
            end_node: node.to_string(),
            ip_address: task.ip_address.clone(),
            request_info: task.request_info.clone(),
            mode: task.mode.as_str().to_string(),
            call_line,
            exit_code,
            outcome: outcome.as_str().to_string(),
            console,
            error_console,
        }
    }
}

/// Ждёт завершения процесса с индивидуальным таймаутом. Возвращает
/// (stdout, stderr, код выхода, был ли таймаут). По таймауту процесс убивается.
fn wait_with_timeout(child: &mut std::process::Child, timeout: Option<Duration>) -> (String, String, i32, bool) {
    // Читаем stdout/stderr в отдельных потоках, иначе переполнение буфера приведёт к
    // взаимоблокировке (процесс ждёт места в pipe, мы ждём его завершения).
    let mut stdout = child.stdout.take();
    let mut stderr = child.stderr.take();
    let out_handle = std::thread::spawn(move || {
        let mut s = String::new();
        if let Some(pipe) = stdout.as_mut() {
            let _ = pipe.read_to_string(&mut s);
        }
        s
    });
    let err_handle = std::thread::spawn(move || {
        let mut s = String::new();
        if let Some(pipe) = stderr.as_mut() {
            let _ = pipe.read_to_string(&mut s);
        }
        s
    });

    let deadline = timeout.map(|t| Instant::now() + t);
    let mut timed_out = false;
    let exit_code = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status.code().unwrap_or(-1),
            Ok(None) => {
                if let Some(d) = deadline {
                    if Instant::now() >= d {
                        let _ = child.kill();
                        let _ = child.wait();
                        timed_out = true;
                        break -1;
                    }
                }
                std::thread::sleep(Duration::from_millis(5));
            }
            Err(_) => break -1,
        }
    };

    let output = out_handle.join().unwrap_or_default();
    let error = err_handle.join().unwrap_or_default();
    (output, error, exit_code, timed_out)
}

/// Пишет итог одного прогона: успех — Info, нештатный исход — Warn.
fn log_run(task: &TaskInfo, node: &str, outcome: RunOutcome, exit_code: i64, elapsed: Duration, summary: &str) {
    let fields = [
        f("название", task.title.clone()),
        f("узел", node),
        f("режим", task.mode.as_str()),
        f("исход", outcome.as_str()),
        f("код", exit_code),
        f("длительность_мс", elapsed.as_millis()),
        f("задача", task.id.clone()),
    ];
    if outcome == RunOutcome::Success {
        LOG.info("Задача выполнена", &fields);
    } else {
        let mut ext = fields.to_vec();
        ext.push(f("причина", first_line(summary)));
        LOG.warn("Задача завершилась нештатно", &ext);
    }
}

/// Разбивает список узлов по «;» или «,».
fn split_nodes(end_node: &str) -> Vec<String> {
    end_node
        .split([';', ','])
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .collect()
}

/// Возвращает аргументы задачи либо аргументы по умолчанию из конфигурации.
fn or_default(params: Vec<String>, default: &str) -> Vec<String> {
    if params.is_empty() {
        default.split_whitespace().map(str::to_string).collect()
    } else {
        params
    }
}

/// Соединяет два непустых текста переводом строки.
fn join_non_empty(a: &str, b: &str) -> String {
    if a.is_empty() {
        b.to_string()
    } else if b.is_empty() {
        a.to_string()
    } else {
        format!("{a}\n{b}")
    }
}

/// Последняя непустая строка (краткий результат для статуса), обрезанная до 300 символов.
fn last_line(text: &str) -> String {
    let line = text.lines().rev().find(|l| !l.trim().is_empty()).unwrap_or("").trim();
    line.chars().take(300).collect()
}

/// Первая непустая строка (краткая причина ошибки для лога).
fn first_line(text: &str) -> String {
    text.lines().map(str::trim).find(|l| !l.is_empty()).unwrap_or("").to_string()
}
