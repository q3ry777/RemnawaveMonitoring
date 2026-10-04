# Remnawave Monitor

Трей-приложение (WPF, .NET 8) для мониторинга панели Remnawave.

Локальная сборка (.NET 8 SDK, Windows):
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

Выпуск новой версии: коммит в main -> тег vX.Y.Z -> GitHub Actions соберёт exe и приложит его к релизу.
Программа сама находит новый релиз (настройки -> обновления программы) и ставит его.

Настройки и кэш: %APPDATA%\RemnawaveMonitor (секреты зашифрованы через DPAPI).
