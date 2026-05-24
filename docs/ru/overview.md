# Обзор

Эта страница дает короткую практическую модель ChokaQ до погружения в код.

Чтобы начать использовать ChokaQ, не нужно заранее понимать каждую внутреннюю
деталь. Сначала разберите базовый flow, запустите пример, а к глубоким статьям
возвращайтесь тогда, когда конкретное runtime-поведение становится важным.

![ChokaQ system context](/diagrams/00-global-system-context.png)

## Что такое background job

Background job - это работа, которую приложение принимает сейчас, но выполняет
вне текущего request.

Пример:

1. Пользователь оформляет заказ.
2. API сохраняет заказ и быстро возвращает ответ.
3. Background job отправляет email с чеком.
4. Если отправка email падает, система может повторить попытку или показать
   ошибку оператору.

Так checkout latency не зависит от email-провайдера, а у системы появляется
место, где можно повторять попытки и позже показывать сбои.

## Зачем хранить задания

Если задание живет только в памяти, оно может исчезнуть при restart процесса.

SQL Server mode сохраняет принятые задания в базе данных. Это значит:

- restart не стирает принятую работу;
- другой воркер может продолжить после падения процесса;
- операторы могут смотреть старые успехи и ошибки;
- retries и delayed jobs живут вне одного процесса.

Поэтому durable mode в ChokaQ начинается с SQL Server.

## Три таблицы заданий

ChokaQ разделяет job data по назначению:

| Таблица | Что хранит | Простое значение |
|---|---|---|
| `JobsHot` | Pending, fetched, processing, retry и delayed jobs. | Работа, которая все еще активна. |
| `JobsArchive` | Успешные задания. | Работа, которая завершилась. |
| `JobsDLQ` | Failed, cancelled, timed-out или zombie jobs. | Работа, которую нужно разобрать или восстановить. |

Такое разделение сохраняет активную работу быстрой. Воркеру не нужно сканировать
годы истории, чтобы найти следующее задание.

Подробнее: [Three Pillars](/ru/1-architecture/three-pillars).

## At-least-once означает, что дубликаты возможны

ChokaQ дает at-least-once execution.

Это значит, что система старается не потерять принятую работу. Но обработчик
может выполниться больше одного раза, если воркер упадет в неудачный момент.

Для безопасных обработчиков:

- повторная отправка того же email должна блокироваться business key;
- повторное списание платежа должно блокироваться idempotency key платежного
  провайдера;
- запись в другую базу должна использовать unique constraint или upsert;
- публикация во внешнюю систему должна использовать outbox или dedupe key там,
  где это нужно.

Перед production-запуском заданий с side effects прочитайте
[Delivery Guarantees](/ru/delivery-guarantees).

## Воркеры забирают работу

Воркеры - это фоновые циклы, которые выполняют задания.

В SQL Server mode воркеры не просто "смотрят" на строки. Они забирают строки
через SQL locking и ownership. Это защищает от ситуации, когда два воркера
намеренно обрабатывают одно и то же задание одновременно.

Читайте [SQL Concurrency](/ru/3-deep-dives/sql-concurrency), если хотите понять
`UPDLOCK`, `READPAST`, ownership и почему именно база данных является границей
координации.

## Delayed jobs и retries - это сохраненное расписание

Delayed job сохраняется сейчас, а становится доступным для выполнения позже.

ChokaQ не нужен отдельный sleeping thread на каждое отложенное задание. Система
сохраняет будущий `ScheduledAtUtc`. Воркеры пропускают строку, пока это время
не наступит.

Retries используют ту же идею. Задание после transient failure может остаться в
`JobsHot` с будущим расписанием, а не спать внутри пользовательского кода.

Контракт durable-поведения описан в [Delivery Guarantees](/ru/delivery-guarantees#delayed-execution).

## The Deck - операторская поверхность

Фоновые системы деградируют, когда за ними никто не наблюдает.

The Deck существует, чтобы операторы могли отвечать на практические вопросы:

- Задания застряли?
- Какая очередь отстает?
- Воркеры живы?
- Какие ошибки повторяются?
- Что находится в DLQ?
- Можно ли безопасно повторить это failed job?

Начните с [Real-time SignalR](/ru/4-the-deck/realtime-signalr), затем переходите к
[Operations Runbooks](/ru/5-operations/runbooks).

## Рекомендуемый порядок чтения

| Шаг | Страница | Зачем |
|---|---|---|
| 1 | [Getting Started](/ru/getting-started) | Запустить систему и увидеть базовую настройку. |
| 2 | [Docker Compose Sample](/ru/samples/docker-compose) | Поднять SQL Server, приложение, The Deck и health checks. |
| 3 | [Delivery Guarantees](/ru/delivery-guarantees) | Понять, что обещает ChokaQ и что должны учитывать обработчики. |
| 4 | [Job Contracts](/ru/job-contracts) | Безопасно проектировать job DTO. |
| 5 | [Package Topology](/ru/1-architecture/package-topology) | Понять, почему пользователи ставят один пакет, а runtime остается модульным. |
| 6 | [Three Pillars](/ru/1-architecture/three-pillars) | Понять Hot, Archive и DLQ. |
| 7 | [SQL Schema Atlas](/ru/3-deep-dives/sql-schema-atlas) | Изучить таблицы и индексы базы данных. |
| 8 | [Transaction Integrity](/ru/3-deep-dives/transaction-integrity) | Понять атомарные переносы Hot/Archive/DLQ и границы at-least-once. |
| 9 | [State Machine](/ru/2-lifecycle/state-machine) | Проследить задание от enqueue до финального состояния. |
| 10 | [Retry And DLQ](/ru/2-lifecycle/retry-and-dlq) | Понять retry budgets, размещение в DLQ и риск resurrection. |
| 11 | [Production Readiness](/ru/5-operations/production-readiness-checklist) | Проверить систему перед production rollout. |
