# CloneDBDLL

Библиотека (.NET Framework 4.6.1 класическа DLL) за клониране на MySQL/MariaDB база данни (минимум версия 5.x). Тя използва `CloneService.CloneDatabase` за копиране на структури, данни, изгледи, тригери и процедури между източник и дестинация.

## Създаване на DLL
1. Уверете се, че имате инсталиран .NET Framework 4.6.1 и инструменти за MSBuild (на Windows) плюс достъп до MySQL сървър поне версия 5.x (MySQL или MariaDB).
2. Възстановете NuGet пакетите (генерира `packages/` и предотвратява грешката за липсващи зависимост/targets). В `NuGet.config` е
зададен `repositoryPath=packages`, така че ако MSBuild/NuGet не открие автоматично папката, добавете `-PackagesDirectory packages`
или стартирайте командите от директорията на проекта:
   ```bash
   nuget restore CloneDBDLL.csproj
   ```
   - алтернатива: `msbuild CloneDBDLL.csproj /t:Restore`
   - изисква се NuGet пакет `MySqlConnector` v2.3.7 (ще създаде `packages\MySqlConnector.2.3.7\...` с `build\net461` цели); проектът автоматично ще пробва и глобалния NuGet cache `$(USERPROFILE)\.nuget\packages\mysqlconnector\2.3.7\...` ако локалната папка липсва
   - ако сте офлайн, свалете ръчно пълния `MySqlConnector.2.3.7.nupkg` в `packages/` и стартирайте `tools/fetch_mysqlconnector.sh`, който ще го разархивира в `packages/MySqlConnector.2.3.7/`; при неуспешно сваляне скриптът връща грешка и не оставя непълен nupkg
3. Билднете в Release:
   ```bash
   msbuild CloneDBDLL.csproj /p:Configuration=Release
   ```
4. Готовата библиотека е в `bin/Release/CloneDBDLL.dll`.

## Употреба
```csharp
var source = new DatabaseConnectionInfo
{
    Host = "source-host",
    Port = 3306,
    UserName = "user",
    Password = "password",
    Database = "source_db"
};

var destination = new DatabaseConnectionInfo
{
    Host = "destination-host",
    Port = 3306,
    UserName = "user",
    Password = "password",
    Database = "destination_db"
};

CloneService.CloneDatabase(
    source,
    destination,
    new CloneOptions
    {
        CopyViews = true,
        CopyTriggers = true,
        CopyRoutines = true
    },
    log: message => Console.WriteLine(message));
```

> Забележка: За да запазите максимална съвместимост с MySQL/MariaDB 5.x, библиотеката използва стандартни SQL команди (`SHOW CREATE TABLE/VIEW/TRIGGER/FUNCTION/PROCEDURE`) без диалектни разширения.
