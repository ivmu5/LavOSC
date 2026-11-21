# LavOSC

![Иконка LavOSC](src/LavOSC/Resources/AppIcon/lavosc.png)

Модульный приёмник OSC на .NET MAUI 10 для Windows x64. Один UDP-приёмник разбирает сообщения OSC и передаёт их зарегистрированным модулям. Модуль «Руль» вычисляет угол по акселерометру телефона и передаёт его на ось X vJoy для ETS 2.

## Сборка на Windows

Нужны .NET 10 SDK и рабочая нагрузка `maui-windows`. Если она уже установлена, начни со второй команды.

```powershell
dotnet workload install maui-windows
dotnet publish .\src\LavOSC\LavOSC.csproj -c Release -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o .\artifacts\publish
.\artifacts\publish\Laveyana.LavOSC.exe
```

Для запуска отладчиком открой `LavOSC.slnx` в Visual Studio с установленной нагрузкой .NET MAUI, выбери конфигурацию **Debug | x64** и назначь `LavOSC` запускаемым проектом с профилем **Windows Machine**.

[Сборка MAUI для Windows](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli?view=net-maui-10.0) · [Релизы vJoy](https://github.com/BrunnerInnovation/vJoy/releases)

## Первый запуск

1. Открой приложение. Слева находятся настройки приёма и модулей. Справа видны IPv4 компьютера, адрес последнего сообщения OSC, счётчики пакетов и состояние руля.
2. В TouchOSC укажи IPv4 компьютера в **Host** и порт из окна приёмника в **Port (outgoing)**. По умолчанию это `8000`.
3. Для руля установи драйвер vJoy с компонентом **Configure vJoy**, включи **Accelerometer (/accxyz)** в TouchOSC и открой экран управления. Подержи телефон в нейтрали примерно секунду и нажми **Выставить центр**. В ручном режиме после калибровки нажми **Включить руль**.
4. Назначь ось X vJoy на руль в настройках ETS 2. Если направления перепутаны, включи **Инверсия руля** слева.

Переключатель **Модуль включён** подключает и отключает модуль «Руль». Общий приём OSC можно оставить включённым. При первом запуске LavOSC импортирует сохранённые настройки прежней версии, если настройки LavOSC ещё не созданы.

## Адреса OSC

| Адрес | Кому доставляется |
| --- | --- |
| `/accxyz` | Всем модулям, подписанным на этот адрес |
| `/wheel/calibrate` | Команде калибровки модуля «Руль» |
| `/whell/calibrate` | Той же команде, как псевдоним из исходного примера |
| `/panel/preset/night/2` | Команде `preset` зарегистрированного модуля `panel`, с сегментами `night` и `2` |

Команды имеют форму `/<модуль>/<команда>[/<данные>...]`. Сегменты после команды доступны в `OscCommand.PathData`. Типизированные аргументы самого OSC-сообщения доступны отдельно, в `OscCommand.Envelope.Message.Arguments`.

Для калибровки добавь в TouchOSC кнопку Push с адресом `/wheel/calibrate`. Команда срабатывает на нажатие со значением `1`, `true`, OSC Impulse или без аргументов. Отпускание со значением `0` игнорируется. Руль принимает команду с того же IPv4, с которого получает акселерометр.

## Подключение нового модуля в C#

Добавь `Laveyana.LavOSC.Core` в проект модуля. Это общий API для модулей и хостов на .NET 10. Реализуй `IOscModule` и в `Configure` объяви команды и подписки.

```csharp
using Laveyana.LavOSC.Core;

public sealed class PanelModule : IOscModule
{
    public string Name => "panel";

    public void Configure(OscModuleRegistration registration)
    {
        registration.Subscribe("/accxyz", packet =>
            Console.WriteLine(string.Join(", ", packet.Message.Arguments)));

        registration.OnCommand("preset", command =>
            Console.WriteLine(string.Join("/", command.PathData)));
    }
}
```

Чтобы подключить опубликованный модуль к приложению LavOSC, добавь его пакет в `src/LavOSC/LavOSC.csproj`. В точке входа `MauiProgram.CreateMauiApp()` зарегистрируй его перед `builder.Build()`.

```csharp
// Требуются using Microsoft.Extensions.DependencyInjection; и using Laveyana.LavOSC.Core;
builder.Services.AddSingleton<IOscModule, PanelModule>();
```

При загрузке окна приложение добавит все `IOscModule` из DI к своему `OscRouter`. Имена `wheel` и `whell` зарезервированы за встроенным рулём. Подключение выполняется при сборке приложения с нужным пакетом.

В собственном хосте можно использовать тот же API напрямую.

```csharp
var router = new OscRouter();
using var registration = router.Register(new PanelModule());
using var receiver = new OscUdpReceiver(8000, router);
```

`IDisposable` из `Register` отключает команды и подписки модуля. У каждого модуля собственная очередь на 64 доставки. Если она заполнится, пропуск учитывается в диагностике. Обработчики одного модуля выполняются по порядку, а ошибки и задержки одного обработчика не останавливают доставку остальным.

## Автоподготовка vJoy

- Приложение ищет `vJoyInterface.dll` в зарегистрированной установке vJoy и в стандартных каталогах Program Files. Поле пути можно оставить пустым или указать путь к 64-битной DLL. Новый путь к DLL вступает в силу после полного перезапуска приложения.
- Если интерфейс vJoy недоступен, приложение читает состояние устройства в Windows. При коде 22 оно пробует включить найденное отключённое устройство через PnPUtil. При других ошибках справа появляется код. После успешной подготовки приложение ищет свободное устройство с осью X. При повторном запуске у последнего использованного устройства приоритет, пока оно доступно.
- Если подходящего устройства нет, приложение передаёт `vJoyConfig.exe` номер отсутствующего устройства и создаёт ось X. Для включения драйвера и создания устройства Windows может запросить разрешение UAC. Создание может кратковременно переподключить устройства vJoy в Windows.
- При ручном выборе номера приложение использует этот номер. Возврат к пункту **Автоматически** включает поиск по всем 16 номерам. Кнопка **Подготовить vJoy** повторяет поиск после установки, изменения настроек или отмены запроса Windows.
- Если DLL выбрана из произвольной папки, автоматическое создание через повышенный процесс доступно для штатной установки vJoy. Устройство с осью X также можно добавить через **Configure vJoy**.
- При пропадании данных с телефона примерно на 350 мс приложение возвращает руль в центр и освобождает виртуальное устройство. После восстановления связи нажми **Включить руль**.

## Настройки руля

- **Угол от центра до упора**. По умолчанию `60°` в каждую сторону, диапазон настройки от `25°` до `110°`.
- **Мёртвая зона**. По умолчанию `2°`. В этом диапазоне приложение отправляет центральное положение оси.
- **Сглаживание**. По умолчанию `55 мс`. Уменьши значение, если нужен более резкий отклик.
- Настройки сохраняются. После нового запуска приёма центр выставляется по текущему хвату.

Если сообщений нет, проверь общий роутер, IPv4 компьютера, порт и разрешение входящего UDP в частной сети Windows. Закрой диагностический приёмник, если он слушает этот же порт. При смене IP телефона останови и снова запусти приём.

## Сборки и пакеты NuGet

| Сборка | Назначение | Пакет NuGet |
| --- | --- | --- |
| `Laveyana.LavOSC` | Интерфейс MAUI и Windows-адаптер к vJoy | — |
| `Laveyana.LavOSC.Core` | Контракты модулей, приём UDP, парсер и маршрутизация OSC | `Laveyana.LavOSC.Core` |
| `Laveyana.LavOSC.Modules.Wheel` | Модуль руля, калибровка и интерфейс `ISteeringOutput` | `Laveyana.LavOSC.Modules.Wheel` |

Для упаковки достаточно .NET 10 SDK. Обе библиотеки можно собрать без MAUI и vJoy.

```powershell
dotnet pack src/LavOSC.Core/LavOSC.Core.csproj -c Release -o artifacts/nuget
dotnet pack src/LavOSC.Modules.Wheel/LavOSC.Modules.Wheel.csproj -c Release -o artifacts/nuget
```

Пакет руля объявляет зависимость от `Laveyana.LavOSC.Core`. Публикуй сначала Core, потом модуль руля. Для отдельного модуля достаточно ссылки на Core, если ему не нужны возможности других модулей.

```powershell
dotnet nuget push artifacts/nuget/Laveyana.LavOSC.Core.0.1.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
dotnet nuget push artifacts/nuget/Laveyana.LavOSC.Modules.Wheel.0.1.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
```


## Публикация на GitHub

Распакуй исходники в отдельную папку и выполни из неё эти команды. Для последней команды нужен [GitHub CLI](https://cli.github.com/) с авторизацией через `gh auth login`.

```powershell
git init -b main
git add .
git commit -m "Initial LavOSC release"
gh repo create LavOSC --public --source=. --remote=origin --push
```

После зелёной сборки на ветке `main` отправь тег для первого релиза.

```powershell
git tag -a v0.1.0 -m "LavOSC v0.1.0"
git push origin v0.1.0
```

GitHub Actions собирает автономную Windows x64 версию и выкладывает архив в артефакты сборки. Для тега `vMAJOR.MINOR.PATCH` тот же workflow создаёт GitHub Release с файлом `LavOSC-win-x64.zip`. Распакуй архив целиком и запусти `Laveyana.LavOSC.exe`.

Отдельное задание сохраняет оба `.nupkg` в артефакте `LavOSC-nuget`. При сборке по тегу версия пакетов берётся из тега. Публикация на NuGet выполняется командами выше.

## Лицензия

Исходный код распространяется по [MIT](LICENSE). Для модуля руля vJoy устанавливается отдельно.
