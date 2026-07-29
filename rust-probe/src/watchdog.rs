//! Сторож связи с сервером — аналог Go RunWatchdog / C# ServerWatchdogService.
//!
//! Если сервер не обращается к пробе дольше Probe:ServerTimeoutHours, проба считает,
//! что её удалили: останавливает все задачи по расписанию и чистит кэш результатов.
//! Любое обращение сервера сбрасывает отсчёт.

use crate::logging::{f, Logger};
use crate::registry::TaskRegistry;
use crate::resultstore::ResultStore;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Duration;

static LOG: Logger = Logger("watchdog");

/// Как часто проверять молчание сервера.
const CHECK_INTERVAL: Duration = Duration::from_secs(60);

/// Отметчик последнего обращения сервера (обновляется HTTP-обработчиками).
pub struct ContactTracker {
    last: AtomicI64, // Unix-время в секундах
}

impl ContactTracker {
    pub fn new() -> Self {
        ContactTracker { last: AtomicI64::new(now_unix()) }
    }

    /// Фиксирует обращение сервера.
    pub fn mark(&self) {
        self.last.store(now_unix(), Ordering::Relaxed);
    }

    /// Время последнего обращения (Unix-секунды).
    pub fn last(&self) -> i64 {
        self.last.load(Ordering::Relaxed)
    }
}

/// Текущее время в секундах Unix.
fn now_unix() -> i64 {
    chrono::Local::now().timestamp()
}

/// Запускает сторож связи в отдельном потоке.
pub fn start(timeout_hours: i64, tracker: Arc<ContactTracker>, tasks: Arc<TaskRegistry>, results: Arc<ResultStore>) {
    if timeout_hours <= 0 {
        LOG.info("Сторож связи выключен (Probe:ServerTimeoutHours = 0)", &[]);
        return;
    }
    LOG.info("Сторож связи запущен", &[f("порог_молчания_ч", timeout_hours)]);

    std::thread::spawn(move || {
        let timeout_secs = timeout_hours * 3600;
        let mut last_cleared: i64 = 0;
        loop {
            std::thread::sleep(CHECK_INTERVAL);

            let last = tracker.last();
            // Уже чистили после этого контакта — ждём следующего обращения сервера.
            if last_cleared >= last || now_unix() - last < timeout_secs {
                continue;
            }

            let stopped = tasks.clear_all();
            results.clear();
            last_cleared = now_unix();
            let when = chrono::DateTime::from_timestamp(last, 0)
                .map(|t| t.with_timezone(&chrono::Local).format("%d.%m.%Y %H:%M").to_string())
                .unwrap_or_default();
            LOG.warn(
                "Сервер молчит дольше порога — проба считает себя удалённой, всё очищено",
                &[f("последний_контакт", when), f("порог_ч", timeout_hours), f("остановлено_задач", stopped)],
            );
        }
    });
}
