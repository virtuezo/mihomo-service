# CODEMAP

Карта кода MihomoService: что где лежит, как устроен поток выполнения и где что менять.
Обзор возможностей и инструкции по запуску — в [README.md](README.md).

## Коротко

Windows-служба (.NET 10, NativeAOT, один `MihomoService.exe`), которая держит запущенным ядро
[mihomo](https://github.com/MetaCubeX/mihomo) в режиме TUN. Служба собирает конфиг ядра из
пользовательского `config.yaml`, проверяет его через `mihomo -t`, ждёт сеть, запускает ядро как дочерний
процесс и следит за ним. Отдельно она держит **kill-switch** — постоянные WFP-фильтры, которые выпускают
в сеть только туннель.

## Структура репозитория

```
MihomoService.slnx                 решение: сервис + тесты
src/MihomoService/                 код службы (один проект, NativeAOT, win-x64)
tests/MihomoService.Tests/         xUnit-тесты (без прав администратора и без сети)
config/config.example.yaml         пример config.yaml с описанием раздела service
scripts/                           сборка и управление службой (*.ps1 + обёртки *.cmd)
bin/                               ядро mihomo-windows-amd64*.exe (в git не хранится)
dist/MihomoService/                результат сборки — готовая к копированию папка (в git не хранится)
```

## Исходный код — `src/MihomoService/`

| Файл | Назначение |
|---|---|
| [Program.cs](src/MihomoService/Program.cs) | Точка входа: разбор командной строки (`check`, `killswitch …`, `mask-config`, `servers`), иначе — хост службы (`Host` + `AddWindowsService`), регистрация зависимостей. Консольный запуск и `killswitch` требуют прав администратора. |
| [Supervisor.cs](src/MihomoService/Supervisor.cs) | `BackgroundService` — главный цикл: kill-switch → ожидание сети → проверка конфига → запуск ядра → надзор. Изменения `config.yaml` считает счётчик (проверенным становится значение до чтения файла — прерванная проверка ничего не теряет); непроверенное изменение проверяется и на лету, и перед каждым запуском ядра. После падения (2 с → 1 мин) без изменений ядро стартует с прежней конфигурацией, `runtime.yaml` и kill-switch сверяются с ней. Повтор отклонённого конфига раз в 2 мин; ошибка слежения перезапускает `FileSystemWatcher`. Фатальная ошибка в службе → `Environment.Exit(1)`, чтобы SCM применил восстановление. |
| [RuntimeConfigBuilder.cs](src/MihomoService/RuntimeConfigBuilder.cs) | Чистая функция `config.yaml → runtime.yaml` (YamlDotNet RepresentationModel, ключи пользователя сохраняются). Fallback-группа, принудительный TUN, значения по умолчанию для rules/DNS, раздел `service:`, адреса TUN для kill-switch, предупреждения. Также `ReadKillSwitchFallback` — настройки kill-switch из битого/неполного конфига (по умолчанию «включён»). |
| [ConfigValidator.cs](src/MihomoService/ConfigValidator.cs) | Собирает конфиг во временный файл, прогоняет `mihomo -t`, при успехе `Commit()` переносит его в `data\runtime.yaml`. У службы и у `check` разные временные файлы и рабочие каталоги ядра (у `check` — временный, пока нет `data\`). Не запустившееся ядро (`Win32Exception`) — отказ проверки, а не падение службы. |
| [CoreRunner.cs](src/MihomoService/CoreRunner.cs) | Запуск `mihomo.exe`: `-v`, `-t`, основной процесс. Вывод ядра → `logs\mihomo.log`. Все процессы ядра помещаются в `JobObject`. |
| [JobObject.cs](src/MihomoService/JobObject.cs) | Job-объект с `KILL_ON_JOB_CLOSE`: ядро умирает вместе со службой, TUN не остаётся без хозяина. |
| [NetworkWaiter.cs](src/MihomoService/NetworkWaiter.cs) | Ожидание сети: маршрут по умолчанию (`GetIpForwardTable2`) через поднятый не-Wintun адаптер с рабочим адресом. Не дольше 2 мин, потом ядро стартует всё равно. |
| [KillSwitch.cs](src/MihomoService/KillSwitch.cs) | `KillSwitchPolicy` и `BuildFilters` — чистое построение набора WFP-фильтров (см. ниже). |
| [KillSwitchManager.cs](src/MihomoService/KillSwitchManager.cs) | Применяет политику в службе: при старте (до всего остального), после каждого применённого конфига и перед перезапуском упавшего ядра; помнит применённое, чтобы не трогать WFP зря. |
| [Wfp.cs](src/MihomoService/Wfp.cs) | Тонкая обёртка над Windows Filtering Platform (`fwpuclnt.dll`): постоянный подслой и до 64 постоянных фильтров с детерминированными ключами; `Replace`/`RemoveAll`/`CountInstalled` в транзакциях. Раскладка структур сверена с `fwpmu.h`. |
| [ConfigEditor.cs](src/MihomoService/ConfigEditor.cs) | Точечная текстовая правка `service.kill-switch` в `config.yaml`: комментарии, форматирование, переводы строк каждой строки (CRLF и LF вперемешку) и BOM сохраняются; не-UTF-8 файл не трогается. Запись через временный файл и `File.Replace` — права доступа `config.yaml` остаются. |
| [ConfigInspector.cs](src/MihomoService/ConfigInspector.cs) | Для `diag`: копия YAML со скрытыми секретами (у прокси, подмешанных в них через `<<:` шаблонов и `listeners` — списки разрешённых ключей, в остальном — запрещённые слова, у URL только схема и адрес) и список прокси-серверов из `config.yaml` и кэша провайдеров (без `path` — `data\proxies\<md5 от url>`, как у mihomo). YAML с циклом из якорей не показывается. |
| [FileLogging.cs](src/MihomoService/FileLogging.cs) | `RotatingLogWriter` (UTF-8 с BOM, ротация 5 МБ → `.1`; если файл держит читатель без `FILE_SHARE_DELETE`, ротация откладывается) и `ILoggerProvider` для `service.log`. Ошибки записи лога никогда не роняют службу. |
| [ServicePaths.cs](src/MihomoService/ServicePaths.cs) | Все пути относительно папки `MihomoService.exe`. |
| [BuildInfo.cs](src/MihomoService/BuildInfo.cs) | Версия для лога, справки и `version`: номер из csproj, коммит (SDK дописывает после `+`) и время сборки (`AssemblyMetadata BuildTime` из csproj) — по хешу не отличить сборки с незакоммиченными правками. |

## Поток выполнения службы

```
SCM → Program (Host) → Supervisor.ExecuteAsync
  ├─ EnsureDirectories
  ├─ KillSwitchManager.ApplyStartup        ← первым: сеть закрыта, даже если дальше всё упадёт
  ├─ проверка mihomo.exe, лог версии
  ├─ FileSystemWatcher(config.yaml)
  └─ цикл:
       NetworkWaiter.WaitAsync            маршрут по умолчанию, ≤ 2 мин
       TryApplyConfigAsync                если конфига ещё нет или config.yaml изменён и не проверен:
                                          Build → runtime.new.yaml → mihomo -t → Commit → KillSwitchManager.Apply
         ├─ отклонён, применённого нет → ждать изменения config.yaml или 2 мин → в начало цикла
         └─ отклонён или не проверялся после падения → runtime.yaml и kill-switch сверяются с прежним
       CoreRunner.Start(runtime.yaml)
       SuperviseAsync:
         ядро упало          → пауза (2 с … 1 мин) → в начало цикла (непроверенное изменение проверится там);
                               код 0 (ядро остановлено сигналом — так бывает при выключении Windows, сигнал
                               приходит раньше остановки службы) пишется в лог только после паузы, предупреждением
         config.yaml изменён → проверка (если ядро упадёт во время неё — проверка прерывается и повторится
                               перед перезапуском ядра);
                               отклонён — ядро работает дальше;
                               runtime не изменился (например, только allow-lan) — без перезапуска;
                               иначе (в том числе kill-switch: он меняет MATCH,REJECT) — перезапуск ядра
                               с уже проверенным конфигом
         остановка службы    → ядро убивается, kill-switch остаётся включённым
  фатальная ошибка → Environment.Exit(1): SCM видит сбой и перезапускает службу (5/10/30 с)
```

## Kill-switch

Набор фильтров для каждого из четырёх слоёв WFP (`ALE_AUTH_CONNECT` / `ALE_AUTH_RECV_ACCEPT`, IPv4 / IPv6),
внутри подслоя срабатывает фильтр с большим весом:

| Вес | Действие | Условие |
|---|---|---|
| 15 | permit | AppId = `mihomo.exe` (нет, если файла ядра нет) |
| 14 | permit | loopback |
| 13 | permit | локальный адрес из сетей TUN (по умолчанию `198.18.0.0/30`, `fdfe:dcba:9876::/126`) |
| 12 | block | удалённый порт 53 — только исходящие слои (нет утечки DNS мимо туннеля) |
| 11 | permit | DHCP (UDP, локальный порт 68 / 546) |
| 10 | permit | link-local, multicast, broadcast |
| 9 | permit | частные сети — если `service.allow-lan: true` |
| 0 | block | всё остальное |

Фильтры постоянные: живут в BFE, действуют после перезагрузки и без запущенной службы. Источник
настройки — `service.kill-switch` в `config.yaml` (нет ключа или конфига → включён). Снимаются
только явно: `killswitch off` (пишет `false` в конфиг) или `killswitch remove` (не трогает конфиг).

## Генерируемый конфиг ядра (`RuntimeConfigBuilder`)

- **Всегда:** `tun.enable`, `tun.auto-route`, `tun.auto-detect-interface` = true; `dns.enable` = true; `mode: rule`.
- **Если не задано:** fallback-группа `PROXY` из всех `proxies` (+ `use` из `proxy-providers`) первой в
  `proxy-groups`; правила «частные сети → DIRECT (REJECT при kill-switch и `allow-lan: false`), `MATCH,PROXY`»;
  `rules` должен быть списком (пустой — как не заданный); `tun.stack: mixed`, `dns-hijack`,
  `tun.inet4-address: 198.18.0.1/30`; DNS: fake-ip, DoH 1.1.1.1/8.8.8.8, `respect-rules: true`
  (+ `proxy-server-nameserver`); `log-level: info`; `ipv6: false`, `dns.ipv6: false`.
- **При kill-switch** последним правилом добавляется `MATCH,REJECT`: mihomo пропускает правило, если прокси не
  передаёт UDP (socks5/ss/vless… без `udp: true`, http), и после последнего правила шлёт соединение в DIRECT —
  UDP (QUIC, STUN) ушёл бы напрямую с настоящим IP, а kill-switch ядру выход разрешает.
- **Якоря** (`&a`, `*a`, `<<: *a`) раскрываются: YamlDotNet превращал повторно объявленный якорь в ссылку на первый узел.
  Якорь, ссылающийся сам на себя (`&a [*a]`), — ошибка конфига: YamlDotNet строит из него цикл (`FindCycle`).
- **Предупреждения:** прокси без UDP (кроме hysteria/hysteria2/tuic/wireguard; `udp` с учётом `<<:` и как его
  читает mihomo — `true` или ненулевое число); `DIRECT` в группе пользователя с тем же именем
  (`GroupGenerated = false`, группа остаётся как есть) при kill-switch; несколько YAML-документов (читается первый).
- **Раздел `service:`** вырезается: `group-name`, `test-url`, `interval`, `timeout`, `kill-switch`, `allow-lan`.

## Файлы во время работы

```
MihomoService.exe, mihomo.exe, config.yaml           рядом друг с другом
data\runtime.yaml        конфиг, с которым запущено ядро (рабочий каталог ядра: -d data)
data\runtime.new.yaml    кандидат при проверке (служба)
data\cache.db, ...       состояние ядра
logs\service.log         служба;  logs\mihomo.log — вывод ядра (оба с ротацией по 5 МБ)
%TEMP%\mihomo-service-check-<pid>.yaml   кандидат команды check (удаляется)
%TEMP%\mihomo-service-check-*\           рабочий каталог ядра для check, пока нет data\ (удаляется)
```

## Командная строка `MihomoService.exe`

| Команда | Что делает | Права |
|---|---|---|
| *(без аргументов)* | служба (через SCM) или консольный режим | админ в консоли |
| `check` | собрать и проверить конфиг, показать группу, kill-switch, сеть; рядом с exe ничего не создаёт | не нужны до установки (потом папку службы открывают только админы) |
| `killswitch on` / `off` | записать `service.kill-switch` в конфиг и применить фильтры (конфиг первым) | админ |
| `killswitch remove` | снять фильтры, конфиг не трогать (удаление службы, аварийное восстановление) | админ |
| `killswitch status` | активны ли фильтры и что записано в конфиге | админ |
| `mask-config <файл> <копия>` | копия YAML со скрытыми секретами (для `diag`) | как к файлу |
| `servers` | `тип<TAB>адрес<TAB>порт` прокси из `config.yaml` и кэша провайдеров (для `diag`) | как к файлу |
| `version` / `--version` | версия: номер, коммит, время сборки (та же строка — первой при старте в `service.log`) | не нужны |

## Скрипты — `scripts/`

| Скрипт | Назначение |
|---|---|
| `build.ps1` / `build.cmd` | тесты → NativeAOT publish → сборка `dist\MihomoService` — только нужное для установки и работы: `MihomoService.exe`, ядро, скрипты службы, `THIRD-PARTY-NOTICES.md` и `mihomo-GPL-3.0.txt` (лицензии того, что идёт вместе со службой; `config.yaml` — свой, через `-Config` или вручную; пример остаётся в `config\`). Отказывается, если служба запущена из `dist`. |
| `install-service.ps1` / `.cmd` | права на папку (отказ, если внутри есть junction/symlink; владелец Администраторы, доступ — только SYSTEM и админам, наследуется всем, что появится в папке позже; `-KeepPermissions` — не трогать) → `check` → регистрация службы (автозапуск, зависимости `Tcpip`, `Nsi`, `BFE`, перезапуск при сбое 5/10/30 с) → `killswitch on` → запуск. |
| `update.ps1` / `.cmd` | обновление установленной службы с GitHub: последние стабильные релизы службы (`virtuezo/mihomo-service`, архив `MihomoService-v<версия>.zip`) и ядра (`MetaCubeX/mihomo`, `amd64-v3`); сверка SHA-256 из API GitHub, `-WhatIf` — только план; копия в `backup\` → остановка → замена → `check` → запуск → ожидание процесса ядра, при сбое — откат. `update.cmd` под самим собой не заменяется (cmd читает идущий батник построчно) — кладётся `update.cmd.new`. |
| `release.ps1` / `.cmd` | только в репозитории: чистое дерево и отправленный HEAD → `build.ps1` → проверка версии собранного exe → `MihomoService-v<Version>.zip` (без `config.yaml`, логов) → `gh release create v<Version>` после подтверждения. |
| `uninstall-service.ps1` / `.cmd` | остановка (не дольше 30 с), отключение автозапуска и удаление службы (результат `sc delete` проверяется; сбой шага — предупреждение, а не выход) → `killswitch remove` (в том числе если службу удалили вручную). |
| `killswitch.ps1` / `.cmd` | `on`/`off`/`status`/`remove` через `MihomoService.exe`. |
| `diag.ps1` / `.cmd` | отчёт о сети в `logs\diag-<время>\`: маршруты и куда реально уходит трафик к интернету и прокси (список серверов — `MihomoService.exe servers`), DNS (fake-ip, DoH, NRPT), системные прокси, внешний IP глазами приложений, kill-switch, логи, конфиги со скрытыми секретами (`mask-config`). Ничего не меняет. |
| `set-region.ps1` / `.cmd` | регион (GeoId, по умолчанию NL) всем профилям — вошедшим, остальным через `reg load NTUSER.DAT` (запись через .NET Registry, выгрузка с повторами и проверкой), Default и `.DEFAULT`; ошибка одного профиля не прерывает остальные. Часовой пояс машины, автоопределение пояса выкл., политика RDP `fEnableTimeZoneRedirection=0`. Язык интерфейса и форматы не трогает. `-WhatIf` — предпросмотр. |

Обёртки `*.cmd` одинаковы, кроме комментария (запускают `.ps1` со своим именем): обход ExecutionPolicy,
самоповышение через UAC (`fltmc`; путь и аргументы — через переменные окружения, код выхода повышенного
скрипта возвращается), окно не закрывается при двойном клике. Скрипты службы копируются в `dist` и ищут `MihomoService.exe` рядом с собой или в
`..\dist\MihomoService`.

## Тесты — `tests/MihomoService.Tests/`

| Файл | Что покрывает |
|---|---|
| `RuntimeConfigBuilderTests.cs` | генерация конфига, раздел `service`, правила по умолчанию и `allow-lan`, DNS по умолчанию, адреса TUN, якоря и `<<:`, ошибки разбора (в том числе циклы якорей) |
| `KillSwitchTests.cs` | набор фильтров, их порядок и веса; размеры структур WFP против `fwpmu.h` |
| `ConfigEditorTests.cs` | точечная правка `service.kill-switch` (отступ раздела, а не вложенных строк, комментарии, CRLF и смешанные переводы строк, BOM, не-UTF-8, права доступа файла, ошибка записи) |
| `ConfigInspectorTests.cs` | маскировка секретов в любом стиле YAML, в шаблонах `<<:` и `listeners`, пароль с `@` в URL, циклы; список прокси-серверов (в том числе из кэша провайдеров, путь mihomo по умолчанию, `<<:`) |
| `NetworkWaiterTests.cs` | разбор `MIB_IPFORWARD_ROW2` (индексы указывают на реальные интерфейсы) |
| `BuildInfoTests.cs` | строка версии содержит коммит и время сборки |
| `RotatingLogWriterTests.cs` | BOM в новом файле и после ротации, отложенная ротация при занятом файле, сбой записи (строка теряется, запись продолжается), запись после `Dispose` |

Не покрыто автотестами (проверяется вручную на машине): применение WFP-фильтров, запуск службы, TUN.

## Где что менять

- **Значения по умолчанию конфига ядра** — константы и `Ensure*`/`ForceTun` в `RuntimeConfigBuilder`.
- **Новая настройка службы** — `ServiceOptions` + `TakeServiceOptions` (+ поле в `RuntimeConfig`), пример в
  `config/config.example.yaml`, раздел в README.
- **Правила kill-switch** — `KillSwitch.BuildFilters`; новые типы условий — `WfpCondition` и
  `Wfp.BuildCondition`. Не больше `Wfp.MaxFilters` (64) фильтров.
- **Тайминги** (перезапуск, ожидание сети, debounce, повтор конфига) — константы в начале `Supervisor` и
  `NetworkWaiter`.
- **Новый релиз** — поднять `<Version>` в `MihomoService.csproj`, закоммитить и отправить, `scripts\release.cmd`.
  Новое ядро — сначала в `bin\` (оттуда его берёт `build.ps1`).
- **Новый скрипт службы** — в список `build.ps1` (оттуда он попадает в `dist` и в архив релиза).

## Подводные камни

- **Kill-switch переживает службу.** Удаление папки без `uninstall-service` оставит машину без интернета;
  восстановление — `MihomoService.exe killswitch remove` из любой копии.
- **`DIRECT` идёт мимо туннеля.** Ядру выход разрешён, поэтому `DIRECT` в правилах или в группе kill-switch
  не блокирует — это решение маршрутизации.
- **WFP-структуры только x64.** Раскладка в `Wfp.cs` проверена тестом для x64; для ARM64/x86 её нужно сверять заново.
- **Сборка NativeAOT** требует MSVC; `build.ps1` добавляет `vswhere.exe` в PATH (без этого `vcvarsall.bat`
  из VS 18 падает).
- **Служба держит файлы `dist`.** Пока она запущена из `dist`, пересборка невозможна. А установка из `dist`
  без `-KeepPermissions` закрывает папку для всех, кроме администраторов, — сборка без повышения прав
  туда больше не пишет.
- **Зависимости и права установки** (`BFE`, ACL) применяются только при новой установке:
  `uninstall-service` → `install-service`.
