# TestJob — HTML processing API

Решение [исходного задания](TASK.md): ASP.NET Core 10, FluentValidation,
AngleSharp, Dapper, PostgreSQL 18. Одна бизнес-функция: `POST /api/process`.

## Запуск

Нужен Docker с Docker Compose. Локальная установка .NET и PostgreSQL не требуется.

```sh
docker compose up -d --wait --wait-timeout 240
```

При первом запуске скачиваются образы и NuGet-пакеты. Исходный код `./src`
монтируется в API-контейнер. При каждом запуске контейнер восстанавливает зависимости
по lock-файлу, собирает проект и запускает полученную сборку в том же контейнере.

- API и Swagger: http://localhost:8090/api/swagger
- Корень http://localhost:8090 перенаправляет в Swagger.
- pgAdmin: http://localhost:8080 — без ввода логина, мастер-пароля и пароля БД.
- Проверка доступности API и БД: http://localhost:8090/health

В pgAdmin раскрыть **TestJob → TestJob PostgreSQL 18 → Databases →
testjob → Schemas → public → Tables → elements**. Подключение уже настроено.

```sh
docker compose logs -f api
docker compose stop
docker compose start --wait
```

Данные PostgreSQL и настройки pgAdmin хранятся в именованных Docker volumes.
Они сохраняются после `stop/start`, `restart` и `down/up`.
Команда `docker compose down -v` удаляет эти данные.

Это локальный стенд с демонстрационными учётными данными в Compose:
`testjob / testjob-local`, БД `testjob`. Веб-порты опубликованы только на
`127.0.0.1`; порт PostgreSQL не опубликован на хосте.

## Проверка запросов

В Swagger раскрыть `POST /api/process`, нажать **Try it out**, вставить содержимое
`json_payload_1.txt` или `json_payload_2.txt`, затем **Execute**.

Или выполнить из корня проекта:

```sh
curl -sS http://localhost:8090/api/process \
  -H 'Content-Type: application/json' \
  --data-binary @json_payload_1.txt

curl -sS http://localhost:8090/api/process \
  -H 'Content-Type: application/json' \
  --data-binary @json_payload_2.txt
```

Фактические ответы API сохранены в [json_result_1.txt](json_result_1.txt)
и [json_result_2.txt](json_result_2.txt).

| Пример | Селектор | Элементов | Email-совпадений |
| --- | --- | ---: | ---: |
| 1 | `a[href]` | 238 | 5 |
| 2 | `script[src]` | 9 | 5 |

В обоих примерах расшифрованный текст:
`AES Error: Object reference not set to an instance of an object.`
Это текст, зашифрованный автором входного примера, а не ошибка нашего API:
в успешном ответе `is_error = 0`.

## Поведение

Все входные поля обязательны. JSON имеет имена полей в snake_case;
ответы возвращаются в UTF-8, с отступами и одинаковым набором полей при успехе и ошибке.
В Swagger поля отмечены как обязательные строки, не допускающие `null`.

Сервис валидирует запрос через FluentValidation, декодирует Base64 и строго проверяет
UTF-8. URL должен быть абсолютным HTTP(S) URL. Он возвращается в ответе, но не
запрашивается по сети. HTML берётся только из `page_b64`; AngleSharp разбирает его
без загрузки внешних ресурсов и выполнения скриптов. Отсутствующие CSS/JS РБК
не влияют на результаты.

Порядок элементов соответствует DOM. Если у выбранного элемента нет запрошенного
атрибута, возвращается пустая строка. Для каждого элемента сохраняются
`attribute_value` и полный `OuterHtml` в таблице `elements`;
`id` — `bigint GENERATED ALWAYS AS IDENTITY`.
Таблица создаётся автоматически. Запись всех элементов одного запроса выполняется
через Dapper в одной транзакции после успешной обработки. Повторный успешный запрос
добавляет новые строки.

Email извлекаются из исходного декодированного HTML с помощью скомпилированного
регулярного выражения с тайм-аутом. Совпадения сохраняются в порядке появления,
включая повторы (поэтому `letters@rbc.ru` встречается дважды). Поддерживаются
ASCII-буквы, цифры, `_`, `%`, `+`, апостроф и дефис в имени; точки разделяют
непустые части имени. Ведущий `+` и апострофы сохраняются. Кавычки, обрамляющие
адрес в HTML-атрибуте, не входят в результат. Доменные метки не могут быть
пустыми, начинаться или заканчиваться дефисом; TLD состоит из 2–63 ASCII-букв.
Это не полная реализация RFC 5322: Unicode-адреса, адреса с quoted local-part
и домены в квадратных скобках не поддерживаются.

AES использует стандартный `System.Security.Cryptography`, ключ ровно 32 байта,
режим ECB и `PaddingMode.None`. Шифротекст должен быть непустым и кратным 16 байтам.
Расшифрованные пробелы и нулевые символы не обрезаются. Этот режим не содержит
проверки целостности: любой ключ допустимой длины не обязательно можно определить
как неверный.

HTTP 200 означает успех, HTTP 400 — ошибку входных данных, HTTP 500 — ошибку
обработки/БД. Во всех ошибочных ответах `is_error = 1`, есть `error_code` и
`error_message`; для неожиданных исключений возвращается `Exception.Message`,
как требует задание.

Основные коды: `MISSING_PARAMETER`, `EMPTY_SELECTOR`, `EMPTY_ATTRIBUTE`,
`INVALID_ATTRIBUTE`, `INVALID_JSON`, `INVALID_URL`, `INVALID_SELECTOR`,
`INVALID_{URL|PAGE|KEY|CIPHERTEXT}_BASE64`, `INVALID_{URL|PAGE|PLAINTEXT}_UTF8`,
`INVALID_KEY_SIZE`, `INVALID_CIPHERTEXT_SIZE`, `DATABASE_ERROR`, `INTERNAL_ERROR`.

## Асинхронность

Контроллер и сервис асинхронны. Используются `ValidateAsync`,
`ParseDocumentAsync`, асинхронное открытие подключения, выполнение SQL,
транзакция и асинхронная запись JSON в HTTP-ответ средствами ASP.NET Core.
CancellationToken запроса передаётся в поддерживающие отмену операции.

Асинхронный ввод-вывод освобождает рабочий поток, пока ожидается БД или клиент:
сервер может обслуживать другие запросы без отдельного заблокированного потока
на каждое ожидание. Это улучшает пропускную способность при конкурентной нагрузке,
но само по себе не ускоряет вычисления.

Base64, UTF-8, CSS-селекторы, Regex и AES работают с данными в памяти и не имеют
настоящих асинхронных аналогов для этих операций. Они выполняются синхронно.
Оборачивать их в `Task.Run` внутри REST API нет смысла: это только переносит
работу на другой поток пула. При разборе HTML из строки `ParseDocumentAsync`
также не превращает CPU-работу в неблокирующий ввод-вывод.

## Интеграционные проверки

После запуска Compose:

```sh
python3 tests/integration.py
```

Скрипт использует только стандартную библиотеку Python и Docker CLI. Он проверяет
оба примера, значения и HTML в реальной БД, схему Swagger, неправильные запросы,
отсутствие записей при ошибке, Unicode, повторяющиеся email, апострофы и ведущий `+`,
адреса в HTML-атрибутах, неверные домены, обязательность полей Swagger, пустую
выборку и отсутствующий атрибут. Перезаписывает файлы результатов фактическими ответами.
Успешные тестовые запросы добавляют строки в локальную БД.

Структура кода: `src/Program.cs` — настройка приложения и обработка исключений;
`ProcessingController.cs` — HTTP-контроллер; `ProcessingService.cs` — обработка
и работа с БД; `Models.cs` — модели и FluentValidation.

Источники настройки: [pgAdmin container deployment](https://www.pgadmin.org/docs/pgadmin4/latest/container_deployment.html),
[PostgreSQL Docker image](https://hub.docker.com/_/postgres).
