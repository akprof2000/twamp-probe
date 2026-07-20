// Тесты контрактов: совместимость JSON с сервером SPI.Twamp.Server.
package main

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

// Задача в формате Flurl/Newtonsoft: PascalCase, enum'ы числами, дата без смещения.
func TestTaskInfo_UnmarshalServerFormat(t *testing.T) {
	body := `{
		"Id": "11111111-2222-3333-4444-555555555555",
		"Title": "test",
		"Type": 1,
		"Mode": 2,
		"CronExpression": "*/5 * * * *",
		"End": "2027-01-01T00:00:00",
		"Start": "2026-07-20T10:00:00+03:00",
		"Parameters": {"all": "-c 300"},
		"RequestInfo": "http://srv:9000"
	}`
	var task TaskInfo
	if err := json.Unmarshal([]byte(body), &task); err != nil {
		t.Fatalf("разбор задачи: %v", err)
	}
	if task.Type != TypeScheduler {
		t.Errorf("Type: ожидался Scheduler, получен %s", task.Type)
	}
	if task.Mode != ModeTWampy {
		t.Errorf("Mode: ожидался TWampy, получен %s", task.Mode)
	}
	if task.End.Year() != 2027 {
		t.Errorf("End: ожидался 2027 год, получен %d", task.End.Year())
	}
	if task.Parameters["all"] != "-c 300" {
		t.Errorf("Parameters не разобраны: %v", task.Parameters)
	}
}

// Enum'ы принимаются и строками в любом регистре.
func TestEnums_AcceptStrings(t *testing.T) {
	var task TaskInfo
	if err := json.Unmarshal([]byte(`{"type":"repeater","mode":"winping"}`), &task); err != nil {
		t.Fatalf("разбор: %v", err)
	}
	if task.Type != TypeRepeater || task.Mode != ModeWinPing {
		t.Errorf("получено Type=%s Mode=%s", task.Type, task.Mode)
	}
}

// Ответы пробы — camelCase, enum'ы строками (как AddNewtonsoftJson + StringEnumConverter).
func TestTaskRunInfo_MarshalCamelCaseStrings(t *testing.T) {
	info := TaskRunInfo{TaskId: "id-1", Title: "t", LastOutcome: OutcomeSuccess}
	data, err := json.Marshal(info)
	if err != nil {
		t.Fatalf("сериализация: %v", err)
	}
	s := string(data)
	if !strings.Contains(s, `"taskId":"id-1"`) || !strings.Contains(s, `"lastOutcome":"Success"`) {
		t.Errorf("неверный формат ответа: %s", s)
	}
}

// Дата сериализуется со смещением и разбирается обратно без потерь (до секунды).
func TestCsTime_RoundTrip(t *testing.T) {
	src := CsTime{time.Date(2026, 7, 20, 12, 30, 45, 0, time.Local)}
	data, _ := json.Marshal(src)
	var parsed CsTime
	if err := json.Unmarshal(data, &parsed); err != nil {
		t.Fatalf("обратный разбор %s: %v", data, err)
	}
	if !parsed.Equal(src.Time) {
		t.Errorf("дата исказилась: %s → %s", src, parsed)
	}
}

// GUID: формат UUID v4 и уникальность.
func TestNewGuid(t *testing.T) {
	a, b := NewGuid(), NewGuid()
	if a == b {
		t.Error("два подряд GUID совпали")
	}
	if len(a) != 36 || strings.Count(a, "-") != 4 {
		t.Errorf("некорректный формат GUID: %s", a)
	}
}
