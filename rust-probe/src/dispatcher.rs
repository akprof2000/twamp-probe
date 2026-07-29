//! Диспетчер зондов: очередь задач + фиксированный пул воркеров — аналог Go Dispatcher
//! и C# ProbeDispatcher. Одновременно выполняется не более MaxParallel зондов,
//! сколько бы задач ни поступило.

use crate::contracts::TaskInfo;
use crate::logging::{f, Logger};
use crate::runner::ProbeRunner;
use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};

static LOG: Logger = Logger("dispatcher");

/// Ёмкость очереди с запасом на массовую заливку: постановка задач не блокирует HTTP.
const QUEUE_CAPACITY: usize = 100_000;

/// Очередь задач и пул воркеров с ограниченной параллельностью.
pub struct Dispatcher {
    queue: Mutex<VecDeque<Arc<TaskInfo>>>,
    signal: Condvar,
    stopping: AtomicBool,
}

impl Dispatcher {
    /// Создаёт диспетчер и поднимает пул воркеров.
    pub fn start(workers: usize, runner: Arc<ProbeRunner>) -> Arc<Dispatcher> {
        let dispatcher = Arc::new(Dispatcher {
            queue: Mutex::new(VecDeque::new()),
            signal: Condvar::new(),
            stopping: AtomicBool::new(false),
        });

        for _ in 0..workers {
            let d = Arc::clone(&dispatcher);
            let r = Arc::clone(&runner);
            std::thread::spawn(move || d.worker_loop(r));
        }

        LOG.info("Пул воркеров запущен", &[f("воркеров", workers), f("ёмкость_очереди", QUEUE_CAPACITY)]);
        dispatcher
    }

    /// Ставит задачу в очередь на выполнение (не блокируя отправителя).
    pub fn enqueue(&self, task: Arc<TaskInfo>) {
        let mut queue = self.queue.lock().unwrap();
        if queue.len() >= QUEUE_CAPACITY {
            LOG.error(
                "Очередь переполнена — задача пропущена",
                &[f("задача", task.id.clone()), f("название", task.title.clone()), f("ёмкость", QUEUE_CAPACITY)],
            );
            return;
        }
        queue.push_back(task);
        drop(queue);
        self.signal.notify_one();
    }

    /// Рабочий цикл: берёт задачи из очереди и выполняет их.
    fn worker_loop(&self, runner: Arc<ProbeRunner>) {
        loop {
            let task = {
                let mut queue = self.queue.lock().unwrap();
                loop {
                    if self.stopping.load(Ordering::Relaxed) {
                        return;
                    }
                    if let Some(t) = queue.pop_front() {
                        break t;
                    }
                    queue = self.signal.wait(queue).unwrap();
                }
            };
            runner.run_for_nodes(&task);
        }
    }

    /// Останавливает пул воркеров (при завершении службы).
    #[allow(dead_code)] // используется при штатной остановке службы
    pub fn stop(&self) {
        self.stopping.store(true, Ordering::Relaxed);
        self.signal.notify_all();
    }
}
