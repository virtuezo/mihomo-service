# Mihomo Service

Прокси-клиент для Windows: служба, которая держит запущенным ядро [mihomo](https://github.com/MetaCubeX/mihomo)
в режиме **TUN** и пускает трафик через **fallback**-группу из прокси, описанных в `config.yaml`.
Системный прокси не настраивается: весь трафик перехватывается TUN-адаптером.

## Как это устроено

```
C:\Program Files\MihomoService\
├── MihomoService.exe   служба-обёртка (.NET 10, NativeAOT, ~6 МБ)
├── mihomo.exe          ядро
├── config.yaml         ваш конфиг: прокси (+ необязательные переопределения)
├── data\               рабочий каталог ядра: runtime.yaml, cache.db, geo-базы
└── logs\               service.log (обёртка), mihomo.log (ядро), ротация по 5 МБ
```

`MihomoService.exe`:

1. читает `config.yaml`, собирает из `proxies` (и `proxy-providers`) fallback-группу `PROXY` в порядке
   объявления, принудительно включает TUN (`auto-route`, `auto-detect-interface`), DNS-модуль и `mode: rule`,
   при отсутствии подставляет правила (локальные сети → DIRECT, остальное → PROXY) и DNS (fake-ip, DoH);
2. проверяет результат через `mihomo -t` и только после этого записывает `data\runtime.yaml`;
3. запускает `mihomo.exe -d data -f data\runtime.yaml` внутри job-объекта: если служба упадёт, ядро
   будет завершено вместе с ней;
4. перезапускает ядро при падении (пауза 2 с → 1 мин);
5. следит за `config.yaml`: изменения применяются на лету, а если новая версия не прошла проверку,
   ядро продолжает работать на прежней, ошибка пишется в `logs\service.log`.

Формат `config.yaml` — обычный конфиг mihomo, см. [config/config.example.yaml](config/config.example.yaml).
Раздел `service:` читает только обёртка (имя группы, URL и интервал проверки), ядру он не передаётся.

## Сборка

Нужны .NET 10 SDK и Visual Studio (или Build Tools) с компонентом «C++ x64/x86 build tools» (для NativeAOT).
Ядро положите в `bin\` (`mihomo-windows-amd64*.exe`; сборка `-v3` требует процессор с AVX2, для старых
CPU берите `mihomo-windows-amd64-compatible`).

```bash
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

Результат — `dist\MihomoService\`. Параметры: `-Core <путь к mihomo.exe>`, `-Config <путь к config.yaml>`.

## Запуск

Проверить конфиг (права администратора не нужны):

```bash
dist\MihomoService\MihomoService.exe check
```

Запустить в консоли для отладки (от администратора, Ctrl+C — остановка):

```bash
dist\MihomoService\MihomoService.exe
```

Установить и удалить службу (временные скрипты для разработки, потом их заменит установщик; запускать от администратора):

```bash
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
```

Служба `MihomoService` запускается автоматически от LocalSystem; при сбое SCM перезапускает её через 5/10/30 с.

## Тесты

```bash
dotnet test MihomoService.slnx
```

## Дальше: установщик

Планируется MSI на WiX (WiX 7 уже стоит как dotnet tool): копирование `dist\MihomoService` в
`Program Files`, `ServiceInstall`/`ServiceControl` с теми же параметрами восстановления, сохранение
`config.yaml` при обновлении и удалении.
