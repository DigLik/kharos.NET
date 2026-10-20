# Эталоны баланса из TS

Вывод харнесса TS (`KHAROS_HARNESS_JSON=1 bun tools/balance.ts all`) — эталоны `BalanceTests` до правки пути в приказе атаки.

В TS каждый приказ атаки снимает путь, даже повтор того же приказа по той же цели. В .NET повтор путь погони оставляет
(`Sections/Combat/Attack.cs`), поэтому эталоны в `Balance/golden/*.json` теперь — вывод харнесса .NET
(`dotnet run -c Release --project dotnet/tools/Kharos.Balance -- all --out <папка>`). Эти файлы — для сверки с TS
(Н7-1 б): их читает `BalanceVsTsTests`, расхождения с причинами — в `DIFF.md`.
