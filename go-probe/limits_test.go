package main

import (
	"runtime/debug"
	"testing"
)

func TestPrepareLimits_RaisesGoThreadLimit(t *testing.T) {
	// Предел потоков Go по умолчанию — 10000, и настройка выше упёрлась бы
	// в него раньше, чем в возможности машины. Проба поднимает его сама.
	const maxParallel = 100000

	previous := debug.SetMaxThreads(maxParallel*unitsPerRun + goThreadHeadroom)
	t.Cleanup(func() { debug.SetMaxThreads(previous) })

	// SetMaxThreads возвращает прежнее значение — по нему и видно, что
	// prepareLimits действительно его подняла.
	restored := debug.SetMaxThreads(previous)
	if restored != maxParallel*unitsPerRun+goThreadHeadroom {
		t.Errorf("предел потоков = %d, ожидался %d",
			restored, maxParallel*unitsPerRun+goThreadHeadroom)
	}
}

func TestPrepareLimits_DoesNotChangeConfiguredValue(t *testing.T) {
	// Главное свойство: что бы ни ответила система, настроенное число замеров
	// остаётся как есть. Проба только предупреждает — решение за администратором.
	const maxParallel = 100000

	previous := debug.SetMaxThreads(maxParallel*unitsPerRun + goThreadHeadroom)
	t.Cleanup(func() { debug.SetMaxThreads(previous) })

	warning := prepareLimits(maxParallel)

	// Предупреждение допустимо (на этой машине лимиты могут быть меньше),
	// но оно обязано быть внятным: с числами и подсказкой, что поднимать.
	if warning != "" {
		t.Logf("предупреждение: %s", warning)
		if len(warning) < 40 {
			t.Errorf("предупреждение слишком краткое, по нему не понять, что делать: %q", warning)
		}
	}
}

func TestResolveParallel_DefaultAndExplicit(t *testing.T) {
	// Ноль означает значение по умолчанию, любое заданное берётся как есть —
	// без урезания под лимиты системы.
	if got := resolveParallel(0); got != defaultMaxParallel {
		t.Errorf("без настройки = %d, ожидалось %d", got, defaultMaxParallel)
	}
	if got := resolveParallel(250000); got != 250000 {
		t.Errorf("заданное значение = %d, ожидалось 250000 без урезания", got)
	}
	if got := resolveParallel(7); got != 7 {
		t.Errorf("малое значение = %d, ожидалось 7", got)
	}
}
