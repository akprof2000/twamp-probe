//! Реестр состояния выполнения задач — аналог Go RunRegistry / C# TaskRunRegistry.
//! Хранит по каждой задаче: сколько запусков идёт, последний старт/финиш, исход,
//! счётчики успехов/ошибок и момент следующего запуска. Питает эндпоинт TaskStatus.

use crate::contracts::{CsTime, RunOutcome, TaskInfo, TaskRunInfo};
use std::collections::HashMap;
use std::sync::Mutex;

/// Реестр статусов выполнения (потокобезопасный).
pub struct RunRegistry {
    states: Mutex<HashMap<String, TaskRunInfo>>,
}

impl RunRegistry {
    pub fn new() -> Self {
        RunRegistry { states: Mutex::new(HashMap::new()) }
    }

    /// Возвращает (создавая при необходимости) запись задачи. Вызывается под замком.
    fn entry<'a>(
        states: &'a mut HashMap<String, TaskRunInfo>,
        task_id: &str,
        title: &str,
        mode: &str,
    ) -> &'a mut TaskRunInfo {
        let info = states.entry(task_id.to_string()).or_insert_with(|| TaskRunInfo {
            task_id: task_id.to_string(),
            title: title.to_string(),
            mode: mode.to_string(),
            running: 0,
            last_start: None,
            last_finish: None,
            executions: 0,
            next_run: None,
            last_outcome: RunOutcome::NotStarted,
            last_exit_code: None,
            last_result: None,
            success_total: 0,
            error_total: 0,
            last_error: None,
        });
        // Обновляем название/режим, если пришли непустыми (первый вызов мог быть без них).
        if !title.is_empty() {
            info.title = title.to_string();
        }
        if !mode.is_empty() {
            info.mode = mode.to_string();
        }
        info
    }

    /// Фиксирует начало запуска зонда.
    pub fn mark_started(&self, task: &TaskInfo) {
        let mut states = self.states.lock().unwrap();
        let info = Self::entry(&mut states, &task.id, &task.title, task.mode.as_str());
        info.running += 1;
        info.last_start = Some(CsTime::now());
        info.executions += 1;
        info.last_outcome = RunOutcome::Running;
    }

    /// Фиксирует исход завершившегося запуска зонда.
    pub fn report_outcome(&self, task_id: &str, outcome: RunOutcome, exit_code: Option<i64>, result: &str) {
        let mut states = self.states.lock().unwrap();
        let info = Self::entry(&mut states, task_id, "", "");
        info.last_outcome = outcome;
        info.last_exit_code = exit_code;
        info.last_result = if result.is_empty() { None } else { Some(result.to_string()) };
        if outcome == RunOutcome::Success {
            info.success_total += 1;
            info.last_error = None; // успех снимает «залипшую» ошибку
        } else {
            info.error_total += 1;
            if !result.is_empty() {
                info.last_error = Some(result.to_string());
            }
        }
    }

    /// Фиксирует завершение выполнения задачи.
    pub fn mark_finished(&self, task_id: &str) {
        let mut states = self.states.lock().unwrap();
        let info = Self::entry(&mut states, task_id, "", "");
        if info.running > 0 {
            info.running -= 1;
        }
        info.last_finish = Some(CsTime::now());
    }

    /// Фиксирует момент следующего запланированного запуска (или снимает его).
    pub fn set_next_run(&self, task_id: &str, title: &str, mode: &str, next: Option<CsTime>) {
        let mut states = self.states.lock().unwrap();
        let info = Self::entry(&mut states, task_id, title, mode);
        info.next_run = next;
    }

    /// Удаляет запись задачи (задача удалена с пробы).
    pub fn remove(&self, task_id: &str) {
        self.states.lock().unwrap().remove(task_id);
    }

    /// Снимок всех записей.
    pub fn all(&self) -> Vec<TaskRunInfo> {
        self.states.lock().unwrap().values().cloned().collect()
    }
}
