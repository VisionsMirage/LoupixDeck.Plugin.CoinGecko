# CoinGecko Price for LoupixDeck

[English](#english) | [日本語](#日本語)

A [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck) plugin that shows the
price and 24-hour change of a crypto pair on a touch button, using the
[CoinGecko](https://www.coingecko.com/) API.

[LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck) のタッチボタンに、
[CoinGecko](https://www.coingecko.com/) API から取得した暗号資産の価格と
24 時間変動率を表示するプラグインです。

---

## English

### Features

**CoinGecko → Price** (`CoinGecko.Price`) is a touch-button display:

```
┌──────────────┐
│   BTC/JPY    │  label (or the pair)
│ ¥12,855,102  │  current price
│    -0.21%    │  24h change: green when up, red when down
└──────────────┘
```

The label and the price are ordinary text layers: select them in the button
editor's **Properties** to change their font, size, colour or position, like
any other text. The 24h change is drawn by the plugin so that its colour can
follow the sign. The button's background is the one set in LoupixDeck.

The two text layers are created when the command is put on a button. A button
that had the command before this layout existed needs the command assigned
again.

Pressing the button opens the coin's page on coingecko.com (the Japanese page
when Windows' display language is Japanese).

### Parameters

| Parameter | Description |
|---|---|
| **Pair** | `COIN/CURRENCY`, e.g. `BTC/JPY`, `ETH/USD`. The coin can be a symbol (`BTC`) or a CoinGecko id (`bitcoin`); a symbol shared by several coins picks the one with the highest market cap. Without `/CURRENCY` the price is in USD. |
| **Label** | Optional. Shown instead of the pair when set. It cannot contain `,` or `)`. |

### Updates and API calls

All buttons share the **Update interval** setting and update together on the
clock: with 5 minutes at :00, :05, :10 …, with 60 minutes on the hour. Each
update fetches every pair on screen in **one** API request, however many
buttons there are. A button that appears between two updates is fetched once
right away and then joins the others. Buttons that are not shown are not
fetched.

With 5 minutes that is 12 requests an hour (about 8,600 a month when running
around the clock), plus one lookup the first time each coin symbol is used.

### Settings

| Setting | Description |
|---|---|
| **Update interval (minutes)** | 5 (default), 10, 15, 30 or 60. Other numbers use the nearest of these. |
| **API key** | Optional. Without a key the public API is used, which can answer with "rate limited" when busy. A [CoinGecko API key](https://www.coingecko.com/en/api/pricing) avoids that: a free Demo key or a key from a paid plan. |
| **Paid plan key** | Turn on when the key is from a paid plan (Analyst or higher); requests then go to the Pro API (`pro-api.coingecko.com`). Leave off for a Demo key. |

**Test connection** checks the API with the current settings and shows
CoinGecko's error message when the key or the plan setting is wrong.

### Display states

| Shows | Meaning |
|---|---|
| `…` | Waiting for the first price (price layer). |
| `N/A` / `Unknown coin` | No coin matches the pair's coin. |
| `N/A` / `Unknown currency` | CoinGecko has no price in that currency. |
| `N/A` / `Fetch failed` | The request failed. A price fetched earlier stays on screen instead. |

### Requirements

- LoupixDeck v1.41.0 or later (Plugin SDK 1.30.0); choosing a font for the
  text layers needs v1.46.0 or later
- Internet access to `api.coingecko.com` (`pro-api.coingecko.com` with a paid plan key)

### Building from source

```powershell
dotnet build -c Release
```

The output is in `bin/Release/net10.0/`. Zip `LoupixDeck.Plugin.CoinGecko.dll`,
`plugin.json` and `icon.png` (all at the root of the zip) and use
**Install from Zip…**. Do not include `LoupixDeck.PluginSdk.dll`; the host
provides it.

### License

[MIT](LICENSE). Price data is provided by [CoinGecko](https://www.coingecko.com/).
This plugin is not affiliated with CoinGecko.

---

## 日本語

### 機能

**CoinGecko → Price**（`CoinGecko.Price`）はタッチボタン用の表示コマンドです。

```
┌──────────────┐
│   BTC/JPY    │  ラベル（未設定なら通貨ペア名）
│ ¥12,855,102  │  現在の価格
│    -0.21%    │  24 時間変動率（プラスは緑、マイナスは赤）
└──────────────┘
```

ラベルと価格は普通のテキストレイヤーです。ボタンエディターの **Properties** で選ぶと、
ほかのテキストと同じようにフォント・サイズ・色・位置を変更できます。
24 時間変動率は、プラス/マイナスで色を変えるためプラグインが描画します。
ボタンの背景は LoupixDeck で設定した背景になります。

2 つのテキストレイヤーは、コマンドをボタンに割り当てたときに作られます。
この構成になる前からコマンドを割り当てていたボタンは、割り当て直してください。

ボタンを押すと、coingecko.com のそのコインのページをブラウザで開きます
（Windows の表示言語が日本語なら日本語ページ）。

### パラメータ

| パラメータ | 内容 |
|---|---|
| **Pair** | `コイン/通貨` の形式。例：`BTC/JPY`、`ETH/USD`。コインはシンボル（`BTC`）でも CoinGecko の ID（`bitcoin`）でも指定できます。同じシンボルのコインが複数あるときは時価総額が最大のものを使います。`/通貨` を省略すると USD 建てになります。 |
| **Label** | 任意。設定するとペア名の代わりに表示します。`,` と `)` は使えません。 |

### 更新タイミングと API 呼び出し回数

すべてのボタンがプラグイン設定の **Update interval** を共有し、時計に合わせて
いっせいに更新します（5 分なら :00、:05、:10 …、60 分なら毎時 0 分）。
1 回の更新では、ボタンがいくつあっても、表示中のすべてのペアを **1 回** の
API リクエストで取得します。更新と更新の間に表示されたボタンは、その場で 1 回
取得してから、次の更新でほかのボタンとそろいます。表示されていないボタンは取得しません。

5 分なら 1 時間に 12 回（24 時間動かして 1 か月で約 8,600 回）です。
このほか、コインのシンボルを初めて使うときに 1 回だけ検索の呼び出しがあります。

### 設定

| 設定 | 内容 |
|---|---|
| **Update interval (minutes)** | 5（既定）、10、15、30、60 のいずれか。それ以外の数値は、いちばん近い値として扱います。 |
| **API key** | 任意。未設定のときは公開 API を使うため、混雑時にレート制限で取得に失敗することがあります。[CoinGecko の API キー](https://www.coingecko.com/ja/api/pricing)（無料の Demo キー、または有料プランのキー）を設定すると避けられます。 |
| **Paid plan key** | 有料プラン（Analyst 以上）のキーのときに ON にします。Pro API（`pro-api.coingecko.com`）を使うようになります。Demo キーのときは OFF のままにします。 |

**Test connection** で、現在の設定で API に接続できるか確認できます。
キーやプランの設定が合っていないときは、CoinGecko のエラーメッセージを表示します。

### 表示の状態

| 表示 | 意味 |
|---|---|
| `…` | 最初の価格を取得中です（価格レイヤー）。 |
| `N/A` / `Unknown coin` | ペアのコインに一致するコインが見つかりません。 |
| `N/A` / `Unknown currency` | その通貨建ての価格が CoinGecko にありません。 |
| `N/A` / `Fetch failed` | 取得に失敗しました。以前に取得した価格があれば、そちらを表示し続けます。 |

### 動作環境

- LoupixDeck v1.41.0 以降（Plugin SDK 1.30.0）。テキストレイヤーのフォント選択は v1.46.0 以降
- `api.coingecko.com`（有料プランのキーでは `pro-api.coingecko.com`）へのインターネット接続

### ソースからビルドする

```powershell
dotnet build -c Release
```

`bin/Release/net10.0/` にできた `LoupixDeck.Plugin.CoinGecko.dll`、`plugin.json`、
`icon.png` を zip にまとめ（すべて zip の直下に置きます）、**Install from Zip…** でインストールします。
`LoupixDeck.PluginSdk.dll` は zip に入れないでください（LoupixDeck 本体に含まれています）。

### ライセンス

[MIT ライセンス](LICENSE)。価格データは [CoinGecko](https://www.coingecko.com/) が提供しています。
このプラグインは CoinGecko とは関係ありません。
