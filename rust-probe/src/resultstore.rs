//! Хранилище результатов с гарантированной доставкой «ровно один раз» — аналог
//! Go ResultStore / C# ResultStore.
//!
//! Результаты копятся в очереди. TakeBatch выдаёт пачку «в полёте» с BatchId и ждёт
//! появления данных до таймаута (длинный опрос). Пачка не удаляется, пока сервер не
//! подтвердит её Confirm — значит, при любом сбое связи данные не теряются и не
//! задваиваются (сервер отбрасывает дубли по ResultId). Недоставленное переживает
//! перезапуск (снимок в файл results.json).

use crate::contracts::{new_guid, ActionData, ResultBatch, EMPTY_GUID};
use crate::logging::{f, Logger};
use std::sync::{Condvar, Mutex};
use std::time::Duration;

const RESULTS_FILE: &str = "results.json";
static LOG: Logger = Logger("results");

/// Внутреннее состояние под замком.
struct State {
    pending: Vec<ActionData>,
    in_flight: Option<Vec<ActionData>>,
    in_flight_id: String,
    dropped: u64,
    dirty: bool,
    stopped: bool,
}

/// Хранилище недоставленных результатов.
pub struct ResultStore {
    state: Mutex<State>,
    signal: Condvar,
    max_pending: usize,
}

impl ResultStore {
    pub fn new(max_pending: usize) -> Self {
        ResultStore {
            state: Mutex::new(State {
                pending: Vec::new(),
                in_flight: None,
                in_flight_id: String::new(),
                dropped: 0,
                dirty: false,
                stopped: false,
            }),
            signal: Condvar::new(),
            max_pending: max_pending.max(1),
        }
    }

    /// Добавляет результат в очередь; при переполнении вытесняет самый старый.
    pub fn add(&self, result: ActionData) {
        let mut st = self.state.lock().unwrap();
        st.pending.push(result);
        if st.pending.len() > self.max_pending {
            st.pending.remove(0);
            st.dropped += 1;
            if st.dropped == 1 || st.dropped % 1000 == 0 {
                LOG.error(
                    "Очередь результатов переполнена — старые записи вытесняются",
                    &[f("лимит", self.max_pending), f("всего_вытеснено", st.dropped)],
                );
            }
        }
        st.dirty = true;
        drop(st);
        self.signal.notify_all();
    }

    /// Выдаёт пачку результатов, ожидая появления данных до timeout (длинный опрос).
    /// Неподтверждённая пачка выдаётся повторно.
    pub fn take_batch(&self, timeout: Duration) -> ResultBatch {
        let mut st = self.state.lock().unwrap();
        loop {
            if let Some(items) = &st.in_flight {
                return ResultBatch { batch_id: st.in_flight_id.clone(), items: items.clone() };
            }
            if !st.pending.is_empty() {
                let items = std::mem::take(&mut st.pending);
                st.in_flight_id = new_guid();
                st.in_flight = Some(items.clone());
                return ResultBatch { batch_id: st.in_flight_id.clone(), items };
            }

            let (guard, wait) = self.signal.wait_timeout(st, timeout).unwrap();
            st = guard;
            if wait.timed_out() || st.stopped {
                return ResultBatch { batch_id: EMPTY_GUID.to_string(), items: Vec::new() };
            }
        }
    }

    /// Подтверждает запись пачки сервером — только теперь она удаляется.
    pub fn confirm(&self, batch_id: &str) -> bool {
        let mut st = self.state.lock().unwrap();
        if st.in_flight.is_none() || st.in_flight_id != batch_id {
            return false;
        }
        st.in_flight = None;
        st.in_flight_id.clear();
        st.dirty = true;
        true
    }

    /// Полностью очищает результаты (очередь, пачку «в полёте», файл на диске).
    /// Используется сторожем связи при «удалении» пробы.
    pub fn clear(&self) {
        let mut st = self.state.lock().unwrap();
        st.pending.clear();
        st.in_flight = None;
        st.in_flight_id.clear();
        st.dirty = false;
        drop(st);
        let _ = std::fs::remove_file(RESULTS_FILE);
    }

    /// Восстанавливает недоставленные результаты после перезапуска.
    pub fn load(&self) {
        let Ok(data) = std::fs::read(RESULTS_FILE) else {
            return; // файла нет — чистый старт
        };
        let saved: Vec<ActionData> = match serde_json::from_slice(&data) {
            Ok(v) => v,
            Err(e) => {
                LOG.error("Не удалось загрузить недоставленные результаты", &[f("файл", RESULTS_FILE), f("ошибка", e)]);
                return;
            }
        };
        if saved.is_empty() {
            return;
        }
        let count = saved.len();
        let mut st = self.state.lock().unwrap();
        let mut merged = saved;
        merged.append(&mut st.pending);
        st.pending = merged;
        drop(st);
        LOG.info("Недоставленные результаты восстановлены с диска", &[f("результатов", count), f("файл", RESULTS_FILE)]);
        self.signal.notify_all();
    }

    /// Атомарно пишет снимок «в полёте + очередь» на диск (только при изменениях).
    pub fn flush(&self) {
        let mut st = self.state.lock().unwrap();
        if !st.dirty {
            return;
        }
        st.dirty = false;
        let mut snapshot: Vec<ActionData> = st.in_flight.clone().unwrap_or_default();
        snapshot.extend(st.pending.iter().cloned());
        drop(st);

        if snapshot.is_empty() {
            let _ = std::fs::remove_file(RESULTS_FILE);
            return;
        }
        let data = match serde_json::to_vec(&snapshot) {
            Ok(d) => d,
            Err(e) => {
                LOG.error("Не удалось сериализовать результаты", &[f("ошибка", e)]);
                return;
            }
        };
        let tmp = format!("{RESULTS_FILE}.tmp");
        if let Err(e) = std::fs::write(&tmp, &data) {
            LOG.error("Не удалось сохранить результаты", &[f("ошибка", e)]);
            return;
        }
        if let Err(e) = std::fs::rename(&tmp, RESULTS_FILE) {
            LOG.error("Не удалось заменить файл результатов", &[f("файл", RESULTS_FILE), f("ошибка", e)]);
        }
    }

    /// Будит ожидающих на длинном опросе (используется при остановке службы).
    #[allow(dead_code)] // используется при штатной остановке службы
    pub fn wake(&self) {
        self.state.lock().unwrap().stopped = true;
        self.signal.notify_all();
    }
}
