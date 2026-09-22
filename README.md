# Link

Link — Windows desktop companion на C#/.NET с интерфейсом WPF и будущей нативной интеграцией с Windows.

## Статус

Сейчас подготовлен базовый foundation проекта: solution, минимальное WPF-приложение и правила разработки. Основные функции Link ещё не реализованы.

## Стек

- C#
- .NET 10
- WPF
- Windows desktop APIs — будут добавляться по мере появления функциональных требований

## Структура

```text
Link.sln
src/
└── Link.Desktop/
    ├── App.xaml
    ├── MainWindow.xaml
    └── Link.Desktop.csproj
```

На текущем этапе оставлен один проект, чтобы не усложнять фундамент до появления реальных границ между модулями. При добавлении функциональности UI, бизнес-логика и Windows-specific интеграции должны оставаться разделёнными внутри понятных компонентов.

## Сборка и запуск

На Windows с установленным .NET 10 SDK:

```powershell
dotnet build Link.sln
dotnet run --project src/Link.Desktop/Link.Desktop.csproj
```

Также solution можно открыть в Visual Studio или JetBrains Rider.
