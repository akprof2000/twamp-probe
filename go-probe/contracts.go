// Контракты обмена с сервером SPI.Twamp.Server — зеркало C#-контрактов пробы.
//
// Тонкости совместимости:
//   - сервер шлёт задачи (SetJobs) через Flurl с настройками Newtonsoft по умолчанию:
//     ключи PascalCase, enum'ы числами — поэтому TaskMode/TaskType принимают и число,
//     и строку, а разбор ключей в Go регистронезависимый сам по себе;
//   - ответы пробы C# сериализует camelCase + enum'ы строками (StringEnumConverter) —
//     Go делает так же, чтобы веб-интерфейс сервера (прокси TaskStatus) не заметил подмены;
//   - даты от Newtonsoft бывают и со смещением, и без — CsTime разбирает оба варианта.
package main

import (
	"crypto/rand"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// --- Guid ---

// NewGuid возвращает новый случайный UUID v4 в строковом виде (как Guid в C#).
func NewGuid() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// EmptyGuid — аналог Guid.Empty.
const EmptyGuid = "00000000-0000-0000-0000-000000000000"

// --- CsTime: дата в формате Newtonsoft ---

// CsTime разбирает даты Newtonsoft: ISO 8601 со смещением и без, с дробными секундами и без.
type CsTime struct{ time.Time }

var csTimeLayouts = []string{
	time.RFC3339Nano,
	time.RFC3339,
	"2006-01-02T15:04:05.999999999", // Kind=Unspecified — без смещения
	"2006-01-02T15:04:05",
}

// UnmarshalJSON разбирает дату в любом из принятых форматов (без смещения — как локальную).
func (t *CsTime) UnmarshalJSON(data []byte) error {
	s := strings.Trim(string(data), `"`)
	if s == "null" || s == "" {
		t.Time = time.Time{}
		return nil
	}
	for _, layout := range csTimeLayouts {
		if parsed, err := time.ParseInLocation(layout, s, time.Local); err == nil {
			t.Time = parsed
			return nil
		}
	}
	return fmt.Errorf("не удалось разобрать дату «%s»", s)
}

// MarshalJSON сериализует дату в ISO 8601 со смещением (Newtonsoft разберёт).
func (t CsTime) MarshalJSON() ([]byte, error) {
	if t.IsZero() {
		return []byte(`"0001-01-01T00:00:00"`), nil
	}
	return []byte(`"` + t.Format("2006-01-02T15:04:05.9999999-07:00") + `"`), nil
}

// --- TaskMode / TaskType: enum'ы, принимающие число и строку ---

// TaskMode — режим зондирования (WinPing / TWamp / TWampy).
type TaskMode string

// Канонические значения TaskMode (порядок совпадает с C#-enum: числовые коды 0/1/2).
const (
	ModeWinPing TaskMode = "WinPing"
	ModeTWamp   TaskMode = "TWamp"
	ModeTWampy  TaskMode = "TWampy"
)

var modeByNumber = map[int]TaskMode{0: ModeWinPing, 1: ModeTWamp, 2: ModeTWampy}

// UnmarshalJSON принимает режим и числом (0/1/2 от Flurl), и строкой (без учёта регистра).
func (m *TaskMode) UnmarshalJSON(data []byte) error {
	s := strings.Trim(string(data), `"`)
	if n, err := strconv.Atoi(s); err == nil {
		if mode, ok := modeByNumber[n]; ok {
			*m = mode
			return nil
		}
		return fmt.Errorf("неизвестный номер режима %d", n)
	}
	for _, mode := range []TaskMode{ModeWinPing, ModeTWamp, ModeTWampy} {
		if strings.EqualFold(s, string(mode)) {
			*m = mode
			return nil
		}
	}
	return fmt.Errorf("неизвестный режим «%s»", s)
}

// MarshalJSON сериализует режим строкой (как StringEnumConverter в C#).
func (m TaskMode) MarshalJSON() ([]byte, error) { return json.Marshal(string(m)) }

// TaskType — тип задачи: разовая (Repeater) или по расписанию (Scheduler).
type TaskType string

// Канонические значения TaskType (числовые коды 0/1 — как в C#-enum).
const (
	TypeRepeater  TaskType = "Repeater"
	TypeScheduler TaskType = "Scheduler"
)

// UnmarshalJSON принимает тип задачи числом (0/1) и строкой (без учёта регистра).
func (t *TaskType) UnmarshalJSON(data []byte) error {
	s := strings.Trim(string(data), `"`)
	if n, err := strconv.Atoi(s); err == nil {
		switch n {
		case 0:
			*t = TypeRepeater
		case 1:
			*t = TypeScheduler
		default:
			return fmt.Errorf("неизвестный номер типа задачи %d", n)
		}
		return nil
	}
	if strings.EqualFold(s, string(TypeRepeater)) {
		*t = TypeRepeater
		return nil
	}
	if strings.EqualFold(s, string(TypeScheduler)) {
		*t = TypeScheduler
		return nil
	}
	return fmt.Errorf("неизвестный тип задачи «%s»", s)
}

// MarshalJSON сериализует тип задачи строкой.
func (t TaskType) MarshalJSON() ([]byte, error) { return json.Marshal(string(t)) }

// --- Контракты ---

// TaskInfo — описание задачи зондирования, получаемое от сервера.
type TaskInfo struct {
	IpAddress       string            `json:"ipAddress"`
	Id              string            `json:"id"`
	Title           string            `json:"title"`
	Type            TaskType          `json:"type"`
	Mode            TaskMode          `json:"mode"`
	CronExpression  string            `json:"cronExpression"`
	CronWithSeconds bool              `json:"cronWithSeconds"`
	ContinueIfError bool              `json:"continueIfError"`
	Repeats         int               `json:"repeats"`
	Circles         int               `json:"circles"`
	PauseSec        uint64            `json:"pauseSec"`
	TimeoutSec      int               `json:"timeoutSec"`
	Start           CsTime            `json:"start"`
	End             CsTime            `json:"end"`
	Create          CsTime            `json:"create"`
	Delete          bool              `json:"delete"`
	EndNode         string            `json:"endNode"`
	Parameters      map[string]string `json:"parameters"`
	RequestInfo     string            `json:"requestInfo"`
}

// ActionData — результат одного замера зонда, передаваемый серверу.
type ActionData struct {
	ResultId     string `json:"resultId"`
	Creation     CsTime `json:"creation"`
	TaskId       string `json:"taskId"`
	EndNode      string `json:"endNode"`
	IPAddress    string `json:"ipAddress"`
	RequestInfo  string `json:"requestInfo"`
	Mode         string `json:"mode"`
	CallLine     string `json:"callLine"`
	ExitCode     *int   `json:"exitCode,omitempty"`
	Outcome      string `json:"outcome"`
	Console      string `json:"console"`
	ErrorConsole string `json:"errorConsole"`
}

// Identify — идентификационные данные пробы (ответ на CheckIn).
type Identify struct {
	IPAddress   string `json:"ipAddress"`
	HostName    string `json:"hostName"`
	MacAddress  string `json:"macAddress"`
	Title       string `json:"title,omitempty"`
	Description string `json:"description,omitempty"`
	RequestInfo string `json:"requestInfo"`
	Version     string `json:"version"`
}

// ResultBatch — пачка результатов с идентификатором для подтверждения (ACK).
type ResultBatch struct {
	BatchId string       `json:"batchId"`
	Items   []ActionData `json:"items"`
}

// RunOutcome — исход запуска зонда (имена совпадают с C#-enum).
type RunOutcome string

// Значения исходов запуска — как в C# RunOutcome.
const (
	OutcomeNotStarted    RunOutcome = "NotStarted"
	OutcomeRunning       RunOutcome = "Running"
	OutcomeSuccess       RunOutcome = "Success"
	OutcomeExitCodeError RunOutcome = "ExitCodeError"
	OutcomeStartFailed   RunOutcome = "StartFailed"
	OutcomeTimedOut      RunOutcome = "TimedOut"
)

// TaskRunInfo — состояние выполнения одной задачи (эндпоинт TaskStatus).
type TaskRunInfo struct {
	TaskId       string     `json:"taskId"`
	Title        string     `json:"title"`
	Mode         string     `json:"mode"`
	Running      int        `json:"running"`
	LastStart    *CsTime    `json:"lastStart,omitempty"`
	LastFinish   *CsTime    `json:"lastFinish,omitempty"`
	Executions   int64      `json:"executions"`
	NextRun      *CsTime    `json:"nextRun,omitempty"`
	LastOutcome  RunOutcome `json:"lastOutcome"`
	LastExitCode *int       `json:"lastExitCode,omitempty"`
	LastResult   *string    `json:"lastResult,omitempty"`
	SuccessTotal int64      `json:"successTotal"`
	ErrorTotal   int64      `json:"errorTotal"`
	LastError    *string    `json:"lastError,omitempty"`
}
