# CloneDBDLL

Библиотека (.NET Framework 4.8 класическа DLL, съвместима с C# 7.3) за клониране на MySQL/MariaDB база данни (минимум версия 5.x). Тя използва `CloneService.CloneDatabase` за копиране на структури, данни, изгледи, тригери и процедури между източник и дестинация.

> **Важно:** Проектът е заключен към езикова версия **C# 7.3** (виж `LangVersion` в `CloneDBDLL.csproj`), за да избегне неволно използване на C# 8+ функции като nullable reference types или `using` declarations, които не са достъпни за .NET Framework 4.8 по подразбиране.

## Създаване на DLL
1. Уверете се, че имате инсталиран .NET Framework 4.8 и инструменти за MSBuild (на Windows) плюс достъп до MySQL сървър поне версия 5.x (MySQL или MariaDB).
2. Възстановете NuGet пакетите (генерира `packages/` и предотвратява грешката за липсващи зависимост/targets). В `NuGet.config` е зададен `repositoryPath=packages`, така че ако MSBuild/NuGet не открие автоматично папката, добавете `-PackagesDirectory packages` или стартирайте командите от директорията на проекта:
   ```bash
   nuget restore CloneDBDLL.csproj
   ```
   - алтернатива: `msbuild CloneDBDLL.csproj /t:Restore`
   - изисква се NuGet пакет `MySqlConnector` v2.3.7 (ще постави DLL в `packages\MySqlConnector.2.3.7\lib\net48`; налични са и резервни варианти `lib\net471`/`lib\net462`)
   - ако сте офлайн, свалете ръчно пълния `MySqlConnector.2.3.7.nupkg` в `packages/` и стартирайте `tools/fetch_mysqlconnector.sh`, който ще го разархивира в `packages/MySqlConnector.2.3.7/`; при неуспешно сваляне скриптът връща грешка и не оставя непълен nupkg; уверете се, че `lib/net48` (или поне `lib/net471`/`lib/net462`) са налични
3. Билднете в Release:
   ```bash
   msbuild CloneDBDLL.csproj /p:Configuration=Release
   ```
4. Готовата библиотека е в `bin/Release/CloneDBDLL.dll`.

## Употреба
```csharp
var sourceConnectionString = "Server=source-host;Port=3306;User Id=user;Password=password;Database=source_db";
var destinationConnectionString = "Server=destination-host;Port=3306;User Id=user;Password=password;Database=destination_db";

await CloneService.CloneDatabaseAsync(
    sourceConnectionString,
    destinationConnectionString,
    new[]
    {
        new TableCloneOption("table1", copyData: true),
        new TableCloneOption("table2", copyData: false) // само структура
    },
    copyTriggers: true,
    copyRoutines: true,
    copyViews: true,
    log: message => Console.WriteLine(message),
    copyMethod: DataCopyMethod.BulkInsert); // или BulkCopy (по подразбиране) с автоматичен fallback към BulkInsert
```

`copyMethod` приема:
- `BulkCopy` (по подразбиране) – опитва се да прехвърли данните чрез `MySqlBulkCopy` и при неуспех автоматично пада към `BulkInsert`.
- `BulkInsert` – директно изпълнява пакетни `INSERT` команди.

### Пример: копиране на цялата база (структура, данни, изгледи, процедури, тригери)
```csharp
var sourceConnectionString = "Server=source-host;Port=3306;User Id=user;Password=password;Database=source_db";
var destinationConnectionString = "Server=destination-host;Port=3306;User Id=user;Password=password;Database=destination_db";

// Зареждаме всички таблици от източника и ги маркираме за копиране на данните им
var tableNames = await CloneService.GetTablesAsync(sourceConnectionString);
var tablesToClone = tableNames
    .Select(name => new TableCloneOption(name, copyData: true))
    .ToArray();

await CloneService.CloneDatabaseAsync(
    sourceConnectionString,
    destinationConnectionString,
    tablesToClone,
    copyTriggers: true,
    copyRoutines: true,
    copyViews: true,
    log: Console.WriteLine,
    copyMethod: DataCopyMethod.BulkCopy,
    createDestinationDatabaseIfMissing: true); // по желание автоматично създава дестинационната база
```

`copyMethod` приема:
- `BulkCopy` (по подразбиране) – опитва се да прехвърли данните чрез `MySqlBulkCopy` и при неуспех автоматично пада към `BulkInsert`.
- `BulkInsert` – директно изпълнява пакетни `INSERT` команди.



> Забележка: За да запазите максимална съвместимост с MySQL/MariaDB 5.x, библиотеката използва стандартни SQL команди (`SHOW CREATE TABLE/VIEW/TRIGGER/FUNCTION/PROCEDURE`) без диалектни разширения.
> Допълнително: връзките се създават с `Allow User Variables=true`, за да се избегнат грешки като `MySqlException: Parameter '@aaaa' must be defined`; не е нужно ръчно да добавяте тази настройка в connection string.
