# CloneDBDLL

Библиотека (.NET 6 класическа DLL) за клониране на MySQL/MariaDB база данни (минимум версия 5.x). Тя използва `CloneService.CloneDatabase` за копиране на структури, данни, изгледи, тригери и процедури между източник и дестинация.

## Създаване на DLL
1. Уверете се, че имате инсталиран .NET 6 SDK и достъп до MySQL сървър поне версия 5.x (MySQL или MariaDB).
2. Изпълнете:
   ```bash
   dotnet build -c Release
   ```
3. Готовата библиотека е в `bin/Release/net6.0/CloneDBDLL.dll`.

## Употреба
```csharp
using CloneDBManager;

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
