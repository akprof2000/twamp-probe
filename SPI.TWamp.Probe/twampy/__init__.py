#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2013-2026 Nokia

"""twampy — реализация TWAMP/TWAMP-light/STAMP.

Python-реализация Two-Way Active Measurement Protocol (TWAMP/TWAMP-light)
по RFC 5357, Simple Two-Way Active Measurement Protocol (STAMP) по RFC 8762
и STAMP Optional Extensions по RFC 8972.

Изначально разработано для валидации Nokia SR OS и SR Linux TWAMP/STAMP.

Форк twampy-fleet добавляет массовый запуск рефлекторов и отправителей
(до ~10000 экземпляров) в одном процессе на asyncio, плюс лаунчер процессов.
Подробности — в README.md и модуле twampy.fleet.
"""

from importlib.metadata import PackageNotFoundError, version

try:
    __version__ = version("twampy-fleet")
except PackageNotFoundError:
    # Пакет не установлен (режим разработки до `pip install -e .`)
    __version__ = "1.3.2+fleet.1"

__author__ = "Sven Wisotzky"
__license__ = "BSD-3-Clause"
__copyright__ = "Copyright (c) 2013-2026 Nokia"

__all__ = []
