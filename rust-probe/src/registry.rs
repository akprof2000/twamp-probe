//! Реестр задач по расписанию + cron-планирование — аналог Go TaskRegistry,
//! C# Worker и CronExecuter.
//!
//! Сервер присылает только изменения (merge_jobs): новые задачи добавляются,
//! существующие обновляются, помеченные Delete — удаляются. Разовые задачи
//! (Repeater) выполняются сразу и в реестре не хранятся. Реестр переживает
//! перезапуск (TaskInfo.json). Единый поток-планировщик будит задачи по времени
//! ближайшего срабатывания cron-выражения.

use crate::contracts::{CsTime, TaskInfo, TaskType};
use crate::dispatcher::Dispatcher;
use crate::logging::{f, Logger};
use crate::runregistry::RunRegistry;
use chrono::{DateTime, Local};
use std::collections::HashMap;
use std::str::FromStr;
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

const TASKS_FILE: &str = "TaskInfo.json";
static LOG: Logger = Logger("registry");

/// Одна задача по расписанию с моментом следующего запуска.
struct Scheduled {
    task: Arc<TaskInfo>,
    next: Option<DateTime<Local>>,
}

/// Реестр задач по расписанию.
pub struct TaskRegistry {
    tasks: Mutex<HashMap<String, Scheduled>>,
    wake: Condvar,
    dispatcher: Mutex<Option<Arc<Dispatcher>>>,
    registry: Arc<RunRegistry>,
}

impl TaskRegistry {
    pub fn new(registry: Arc<RunRegistry>) -> Arc<Self> {
        Arc::new(TaskRegistry {
            tasks: Mutex::new(HashMap::new()),
            wake: Condvar::new(),
            dispatcher: Mutex::new(None),
            registry,
        })
    }

    /// Привязывает диспетчер (создаётся позже реестра) и запускает поток-планировщик.
    pub fn start(self: &Arc<Self>, dispatcher: Arc<Dispatcher>) {
        *self.dispatcher.lock().unwrap() = Some(dispatcher);
        let me = Arc::clone(self);
        std::thread::spawn(move || me.scheduler_loop());
    }

    /// Ставит задачу в очередь диспетчера.
    fn enqueue(&self, task: Arc<TaskInfo>) {
        if let Some(d) = self.dispatcher.lock().unwrap().as_ref() {
            d.enqueue(task);
        }
    }

    /// Восстанавливает реестр задач с диска.
    pub fn load(self: &Arc<Self>) {
        let Ok(data) = std::fs::read(TASKS_FILE) else {
            return; // файла нет — чистый старт
        };
        let saved: Vec<TaskInfo> = match serde_json::from_slice(&data) {
            Ok(v) => v,
            Err(e) => {
                LOG.error("Не удалось загрузить реестр задач", &[f("файл", TASKS_FILE), f("ошибка", e)]);
                return;
            }
        };
        let count = saved.len();
        self.merge_jobs(saved);
        if count > 0 {
            LOG.info("Реестр задач восстановлен с диска", &[f("файл", TASKS_FILE), f("задач", count)]);
        }
    }

    /// Сливает присланные изменения задач в реестр.
    pub fn merge_jobs(self: &Arc<Self>, jobs: Vec<TaskInfo>) {
        let mut changed = false;
        {
            let mut tasks = self.tasks.lock().unwrap();
            for job in jobs {
                changed |= self.merge_one(&mut tasks, job);
            }
            if changed {
                persist(&tasks);
            }
        }
        if changed {
            self.wake.notify_all(); // планировщик пересчитает ближайший срок
        }
    }

    /// Применяет одну задачу; вызывается под замком. Возвращает «реестр изменился».
    fn merge_one(&self, tasks: &mut HashMap<String, Scheduled>, item: TaskInfo) -> bool {
        // Разовые задачи выполняем немедленно и в реестре не храним.
        if item.task_type == TaskType::Repeater {
            if !item.delete {
                self.enqueue(Arc::new(item));
            }
            return false;
        }

        // Задача по расписанию, помеченная на удаление.
        if item.delete {
            if tasks.remove(&item.id).is_some() {
                self.registry.remove(&item.id);
                LOG.info("Задача удалена", &[f("задача", item.id.clone()), f("название", item.title.clone())]);
                return true;
            }
            return false;
        }

        let existed = tasks.contains_key(&item.id);
        let task = Arc::new(item);
        let next = self.compute_next(&task);
        tasks.insert(task.id.clone(), Scheduled { task: Arc::clone(&task), next });

        if existed {
            LOG.debug("Задача обновлена", &[f("задача", task.id.clone()), f("название", task.title.clone())]);
        } else {
            LOG.debug(
                "Задача добавлена",
                &[
                    f("задача", task.id.clone()),
                    f("название", task.title.clone()),
                    f("режим", task.mode.as_str()),
                    f("узел", task.end_node.clone()),
                    f("расписание", task.cron_expression.clone()),
                ],
            );
        }
        true
    }

    /// Вычисляет момент следующего запуска по cron-выражению задачи.
    fn compute_next(&self, task: &TaskInfo) -> Option<DateTime<Local>> {
        // Крейт cron ждёт 6–7 полей (с секундами); классическое 5-польное выражение
        // дополняем секундами «0», как это делает парсер Go-пробы.
        let expr = if task.cron_with_seconds {
            task.cron_expression.clone()
        } else {
            format!("0 {}", task.cron_expression.trim())
        };

        let schedule = match cron::Schedule::from_str(&expr) {
            Ok(s) => s,
            Err(e) => {
                LOG.error(
                    "Некорректное cron-выражение — задача не будет запускаться",
                    &[
                        f("задача", task.id.clone()),
                        f("название", task.title.clone()),
                        f("выражение", task.cron_expression.clone()),
                        f("ошибка", e),
                    ],
                );
                self.registry.set_next_run(&task.id, &task.title, task.mode.as_str(), None);
                return None;
            }
        };

        let next = schedule.upcoming(Local).next();
        match next {
            Some(when) => {
                // Задача, у которой вышел срок окончания, больше не планируется.
                if let Some(end) = task.end.0 {
                    if when > end {
                        LOG.info(
                            "Задача завершена по дате окончания",
                            &[
                                f("задача", task.id.clone()),
                                f("название", task.title.clone()),
                                f("окончание", end.format("%d.%m.%Y %H:%M")),
                            ],
                        );
                        self.registry.set_next_run(&task.id, &task.title, task.mode.as_str(), None);
                        return None;
                    }
                }
                // Фиксируем план в реестре статусов — оператор видит, когда следующий запуск.
                self.registry.set_next_run(&task.id, &task.title, task.mode.as_str(), Some(CsTime(Some(when))));
                Some(when)
            }
            None => {
                self.registry.set_next_run(&task.id, &task.title, task.mode.as_str(), None);
                None
            }
        }
    }

    /// Поток-планировщик: спит до ближайшего срабатывания, ставит задачи в очередь
    /// и сразу планирует следующий запуск (не дожидаясь завершения зонда).
    fn scheduler_loop(self: Arc<Self>) {
        loop {
            let mut tasks = self.tasks.lock().unwrap();

            // Ближайший срок среди всех задач.
            let now = Local::now();
            let mut due: Vec<String> = Vec::new();
            let mut nearest: Option<DateTime<Local>> = None;
            for (id, entry) in tasks.iter() {
                match entry.next {
                    Some(when) if when <= now => due.push(id.clone()),
                    Some(when) => nearest = Some(nearest.map_or(when, |n: DateTime<Local>| n.min(when))),
                    None => {}
                }
            }

            // Наступившие задачи — в очередь, следующий запуск планируем сразу.
            for id in due {
                let task = match tasks.get(&id) {
                    Some(entry) => Arc::clone(&entry.task),
                    None => continue,
                };
                self.enqueue(Arc::clone(&task));
                let next = self.compute_next(&task);
                if let Some(entry) = tasks.get_mut(&id) {
                    entry.next = next;
                    if let Some(when) = next {
                        nearest = Some(nearest.map_or(when, |n: DateTime<Local>| n.min(when)));
                    }
                }
            }

            // Спим до ближайшего срока (но не дольше секунды — чтобы реагировать на изменения).
            let sleep = nearest
                .map(|when| (when - Local::now()).to_std().unwrap_or(Duration::from_millis(1)))
                .unwrap_or(Duration::from_secs(1))
                .min(Duration::from_secs(1));
            let _ = self.wake.wait_timeout(tasks, sleep).unwrap();
        }
    }

    /// Идентификаторы задач по расписанию (для сверки сервером).
    pub fn known_task_ids(&self) -> Vec<String> {
        self.tasks.lock().unwrap().keys().cloned().collect()
    }

    /// Полные определения задач по расписанию (сервер забирает их для восстановления БД).
    pub fn all_tasks(&self) -> Vec<TaskInfo> {
        self.tasks.lock().unwrap().values().map(|e| (*e.task).clone()).collect()
    }

    /// Останавливает и удаляет ВСЕ задачи вместе с файлом реестра.
    /// Используется сторожем связи: сервер молчит дольше таймаута — проба считает себя
    /// удалённой. Возвращает число остановленных задач.
    pub fn clear_all(&self) -> usize {
        let mut tasks = self.tasks.lock().unwrap();
        let count = tasks.len();
        for id in tasks.keys() {
            self.registry.remove(id);
        }
        tasks.clear();
        let _ = std::fs::remove_file(TASKS_FILE);
        count
    }
}

/// Сохраняет реестр на диск; вызывается под замком.
fn persist(tasks: &HashMap<String, Scheduled>) {
    let all: Vec<&TaskInfo> = tasks.values().map(|e| e.task.as_ref()).collect();
    let data = match serde_json::to_vec(&all) {
        Ok(d) => d,
        Err(e) => {
            LOG.error("Не удалось сериализовать реестр задач", &[f("ошибка", e)]);
            return;
        }
    };
    let tmp = format!("{TASKS_FILE}.tmp");
    if let Err(e) = std::fs::write(&tmp, &data) {
        LOG.error("Не удалось сохранить реестр задач", &[f("файл", TASKS_FILE), f("ошибка", e)]);
        return;
    }
    if let Err(e) = std::fs::rename(&tmp, TASKS_FILE) {
        LOG.error("Не удалось заменить файл реестра задач", &[f("файл", TASKS_FILE), f("ошибка", e)]);
    }
}
