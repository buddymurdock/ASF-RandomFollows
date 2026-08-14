# ASF-RandomFollows

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который через случайные интервалы подписывает бота ("Follow") на случайные игры из Steam-витрины (New Releases/Specials) и, опционально, на заданные кураторы — облегчённый аналог [ASF-RandomWishlistAdditions](https://github.com/buddymurdock/ASF-RandomWishlistAdditions): Follow не несёт сигнала "собираюсь купить", это просто подписка на уведомления/обновления.

Использует те же эндпоинты, что и кнопка "Follow" на странице игры/куратора в Steam Store (`store.steampowered.com/explore/followgame/`, `store.steampowered.com/curators/ajaxfollow`) — тот же публичный API, который уже используется рабочим плагином [ASFEnhance](https://github.com/chr233/ASFEnhance) для команд `FOLLOWGAME`/`FOLLOWCURATOR`, здесь просто автоматизирован по случайному расписанию.

Пауза между попытками задаётся диапазоном `[MinDelayMinutes; MaxDelayMinutes]` минут, но это **не жёсткие границы** — задержка берётся из клэмпированного лог-нормального распределения (min/max ≈ 5-й/95-й перцентиль, медиана `sqrt(min*max)`), а не uniform.

## Источники

- **Игры** (`FollowGames`, по умолчанию включено) — кандидаты берутся из тех же публичных виджетов Steam-витрины (New Releases + Specials), что и у RandomWishlistAdditions, с фильтрацией уже принадлежащих боту и не-игр (DLC/саундтрек — проверка через `appdetails`). У каждого бота — случайная цель `[GamesTargetMinCount; GamesTargetMaxCount]` на весь процесс, после достижения — больше не фолловит игры.
- **Кураторы** (`FollowCurators`, по умолчанию выключено) — только из заданного списка `CuratorClanIDs` (реальные SteamID64 кураторов, оператор должен указать сам — без бандла/автообнаружения, как и у [ASF-RandomGroupJoins](https://github.com/buddymurdock/ASF-RandomGroupJoins) до того, как в него добавили опциональный бандл). У каждого бота — случайная цель `[CuratorsTargetMinCount; CuratorsTargetMaxCount]` (не больше размера списка).

Если включены оба источника — на каждую попытку случайно выбирается, какой источник пробовать первым (с фолбэком на другой), чтобы фиксированный порядок сам по себе не был паттерном.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomFollowsEnabled": true,
	"RandomFollowsMinDelayMinutes": 360,
	"RandomFollowsMaxDelayMinutes": 1440,
	"RandomFollowsFollowGames": true,
	"RandomFollowsGamesTargetMinCount": 3,
	"RandomFollowsGamesTargetMaxCount": 10,
	"RandomFollowsCandidatePoolCacheHours": 6,
	"RandomFollowsFollowCurators": false,
	"RandomFollowsCuratorClanIDs": [],
	"RandomFollowsCuratorsTargetMinCount": 0,
	"RandomFollowsCuratorsTargetMaxCount": 5
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomFollowsEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomFollowsMinDelayMinutes` | `ushort` | `360` | Нижняя граница (≈5-й перцентиль) случайной паузы между попытками, в минутах. |
| `RandomFollowsMaxDelayMinutes` | `ushort` | `1440` | Верхняя граница (≈95-й перцентиль) случайной паузы. |
| `RandomFollowsFollowGames` | `bool` | `true` | Включает источник "случайные игры". |
| `RandomFollowsGamesTargetMinCount` | `byte` | `3` | Нижняя граница случайной цели по числу зафолловленных игр на бота. |
| `RandomFollowsGamesTargetMaxCount` | `byte` | `10` | Верхняя граница. |
| `RandomFollowsCandidatePoolCacheHours` | `ushort` | `6` | Как часто (в часах) обновлять общий на все боты пул кандидатов-игр. |
| `RandomFollowsFollowCurators` | `bool` | `false` | Включает источник "кураторы" — требует непустой `CuratorClanIDs`. |
| `RandomFollowsCuratorClanIDs` | `ulong[]` (SteamID64) | `[]` | Список кураторов, из которого выбираются случайные подписки. Пуст по умолчанию — свой список нужно указать явно. |
| `RandomFollowsCuratorsTargetMinCount` | `byte` | `0` | Нижняя граница случайной цели по числу зафолловленных кураторов на бота. |
| `RandomFollowsCuratorsTargetMaxCount` | `byte` | `5` | Верхняя граница (автоматически ограничена размером `CuratorClanIDs`). |

Если `Min` больше `Max` в любой из пар — значения меняются местами автоматически.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomFollows.git
cd ASF-RandomFollows
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
