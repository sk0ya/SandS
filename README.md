# SandS

AutoHotkey の SandS + 周辺のキーカスタマイズをまとめて置き換える常駐ソフト。Windows 専用。
タスクトレイに常駐し、右クリックで有効/無効・設定の再読み込み・スタートアップ登録ができます。

**実装が 2 つあります。**

| | `sands-rs/` (Rust) | `SandS/` (C# NativeAOT) |
| --- | --- | --- |
| exe | **0.28 MB** | 2.07 MB |
| Working Set | **8.2 MB** | 10.9 MB |
| Private | **1.5 MB** | 4.3 MB |
| スレッド | **4** | 5 |
| ビルドに要るもの | Rust + MSVC リンカ | .NET 9 SDK + VS C++ ビルドツール |

挙動と設定ファイルは同一で、**同じ E2E テストの両方を通しています**。
Rust 版が本命で、C# 版は移植元として残してあります。

扱う機能は 3 つです。

| 機能 | 例 | AHK での書き方 |
| --- | --- | --- |
| プレフィックスキー | Space 単打でスペース、押しながらで Shift | `Space & x::` + `Space::` |
| ホットキー | `Alt+;` → `Ctrl+F12` | `!sc027::Send "^{F12}"` |
| 単純リマップ | カタカナ/ひらがな → 半角/全角 | `sc070::Send "{sc029}"` |

## ビルドと実行 (Rust 版)

```
cd sands-rs
cargo build --release
target\release\sands.exe
```

`cargo test` がパーサ・設定・アイコン・タスク XML を検証します
(実キー挙動は下記の E2E が受け持ちます)。

## ビルドと実行 (C# 版)

成果物は **NativeAOT でビルドした単一ネイティブ exe (約 2MB)** です。.NET ランタイムの導入は要りません。

```
dotnet publish SandS\SandS.csproj -c Release
SandS\bin\Release\net9.0-windows\win-x64\publish\SandS.exe
```

AOT のネイティブリンクには Visual Studio の C++ ビルドツールが必要です。
`vswhere.exe' は認識されていません` で失敗する場合は PATH を通してください。

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
```

開発中は `dotnet build` でも動きます (AOT は publish 時のみ)。
`--config <path>` で設定ファイルを指定できます (省略時は exe と同じ場所の `sands.config.json`)。

## 設定

`sands.config.json` を編集し、トレイメニューの「設定を再読み込み」または **BackSpace+R** で反映されます。
ファイルが無ければ初回起動時に既定値 (元の AHK スクリプトの内容) で生成されます。

### PrefixKeys

単打では本来のキー、押しながらだと別の割り当てになるキー。

```jsonc
{
  "Key": "BackSpace",          // プレフィックスにするキー
  "Tap": "BackSpace",          // 単打で送るもの。null なら何も送らない
  "HoldModifier": null,        // Map に無いキー全部に付ける修飾キー (SandS は "LShift")
  "TapTimeoutMs": 0,           // 単打とみなす最大押下時間。0 で無制限
  "Map": {                     // 押しながらのキー別割り当て
    "h": "{Blind}Left"
  }
}
```

### Hotkeys / キーコンボの書き方

```jsonc
"Hotkeys": {
  "!sc027": "^F12",            // Alt+; → Ctrl+F12
  "sc070": "sc029"             // 単純リマップ
}
```

- 修飾キー: `^` Ctrl / `!` Alt / `+` Shift / `#` Win
- キー名: `.NET の Keys` 名 + 別名 (`BackSpace`, `Enter`, `Esc`, `AppsKey`, `Muhenkan`, `Henkan` など)
- スキャンコード指定: `sc027` のように書くと**物理位置**で判定・送出します。
  日本語配列で仮想キーコードが揺れるキー (`sc029` = 半角/全角 など) はこちらが確実です。
  拡張キーは AHK と同じく `sc14B` のように 0x100 のビットを立てます。
- `{Blind}` を先頭に付けると、物理的に押している修飾キーをそのまま残して送ります。
  付けない場合は AHK の既定と同じく、コンボに含まれない修飾キーを一時的に外して送ります。
  例: `BackSpace & h → "{Blind}Left"` は Shift 押下中なら Shift+Left (選択) になります。
- `@reload` / `@edit` / `@toggle` / `@exit` はキーではなくアプリへの命令です
  (元スクリプトの `BackSpace & R::Reload` / `BackSpace & e::Edit` に相当)。

## 元の AHK スクリプトとの差分

- **`Shift & Enter` / `Ctrl & Enter` の個別定義は不要**です。修飾キーが押されている間は
  プレフィックス扱いをしないので、Shift+Enter や Ctrl+Enter は素通しで元どおり動きます。
- **`LAlt & H` は `!h` として書きます**。LAlt は修飾キーなのでプレフィックスではなくホットキーです。
  左右の Alt を区別せず、RAlt+H でも発火します。
- **Space を押した瞬間には Shift を送りません**(遅延 Shift)。押下時に即 Shift を送ると
  Space 単打が `Shift↓ Space Shift↑` になり、日本語 IME が Shift+Space = 全角スペースと
  解釈してしまうためです。他キーが来て初めて Shift を注入します。
- **プレフィックスキーは長押ししても連射しません**(AHK と同じ)。BackSpace を押しっぱなしにしても
  1 文字しか消えません。

## 実装メモ

`WH_KEYBOARD_LL` の低レベルフックと `SendInput`。効いている工夫は 3 つです。

**元イベントの破棄と再注入** — プレフィックス保持中に来たキーは、元イベントを破棄して
「修飾キー down → そのキー down」の順で送り直します。フック内で `SendInput` してから
元イベントを素通しすると、修飾キーとキーの到着順が保証されないためです。

**Alt/Win のマスキング** — Alt を押した直後に何も挟まずに離すと Windows がメニューモードに入り、
後続のキーがメニューに food として食われて宛先に届きません。そのため `{Blind}` でない送出で
Alt/Win を外す必要があるときは、先にコンボ側の修飾キー (無ければ無害な Ctrl 打鍵) を挟んでから
外します。外した Alt/Win は押し直さず、ユーザーが実際に指を離したときの key up を握り潰します
(押し直すと、そのあとの物理的な離しが再び「単独押し」になってメニューが開くため)。

**ホットパスでの確保ゼロ** — フックのコールバックは全打鍵で走り、`LowLevelHooksTimeout`
(既定 300ms) を超えると Windows にフックごと無効化されます。そのため
`KBDLLHOOKSTRUCT` は `Marshal.PtrToStructure` ではなく直接読み、`SendInput` はポインタ版を使い、
修飾キーの状態は `HashSet` ではなく 1 バイトのビットマスクで持ち、LINQ とイテレータを排しています。

自分が注入したイベントは `dwExtraInfo` に目印 (`0x5A4D5344`) を付けてフック側で読み飛ばします。

### メモリ

常駐アプリなので、**WinForms と System.Drawing を一切参照していません**。参照した時点で SDK が
トリミングと NativeAOT を拒否する (`NETSDK1175`) ためです。トレイアイコン、ポップアップメニュー、
メッセージボックス、メッセージループ、仮想キーコードの列挙 (`Vk`) はすべて自前の Win32 です。
アイコンは System.Drawing を使わず 32bpp のピクセルを直接塗って GDI に渡しています。

JSON もソース生成した `JsonTypeInfo` 経由でのみ読み書きします
(`JsonSerializer.Serialize<T>()` の型引数版はリフレクションを要求し AOT で動きません)。

実測値:

| | WinForms 版 | NativeAOT + 素の Win32 |
| --- | --- | --- |
| exe | ランタイム必須 | **2.07 MB** 単一ファイル |
| Working Set | 39.9 MB | **10.2 MB** |
| Private | 10.0 MB | **3.8 MB** |
| スレッド | 13 | **5** |

## 管理者権限について

非昇格プロセスのフックは、昇格したアプリへの入力に介入できません。つまり普通に起動すると、
**管理者として実行しているウィンドウの上でだけ SandS が効きません**。

トレイメニューの「Windows 起動時に開始」は、この事情に合わせて登録先を変えます。

| SandS の起動状態 | 登録先 | 管理者ウィンドウ上で効くか |
| --- | --- | --- |
| 管理者として実行中 | タスクスケジューラ (最上位の特権) | ○ |
| 通常 | `HKCU\Run` | × |

`HKCU\Run` には昇格して起動する手段がない (あれば毎回 UAC が出てしまう) ので、
どこでも効かせたい場合は **SandS を一度「管理者として実行」してから登録し直してください**。
タスクとして登録され、次回ログオンからは UAC なしで昇格起動します。
登録の解除にも管理者権限が必要です。

いま昇格しているかはトレイのツールチップ (「SandS — 有効 (管理者)」) と、
メニューの「Windows 起動時に開始 — 管理者 / 通常」の表示で分かります。

## 制限
- 他のキーカスタマイズソフト (AutoHotkey、keyhac など) が同じキーを掴んでいると奪い合いになります。
  併用せず、どちらか一方にしてください。
- 送出は合成入力 (`LLKHF_INJECTED`) なので、合成入力を弾くゲーム/アンチチートでは効きません。

## テスト

`tests\SandS.E2E` は SandS.exe を実際に常駐させて合成キー入力を流し込み、
コントロールに届いた文字と修飾キーを観測する E2E テストです。

```
dotnet publish SandS\SandS.csproj -c Release
dotnet build tests\SandS.E2E\SandS.E2E.csproj -c Release
tests\SandS.E2E\bin\Release\net9.0-windows\SandS.E2E.exe   # 結果は results.txt へ
```

exe の起動と終了はテスト側が行います。
**他のキーカスタマイズソフトを止めてから実行してください** (掴み合いで結果が壊れます)。

Part 2 は exe をブラックボックスとして観測するだけなので**実装言語に依存しません**。
`SANDS_EXE` で対象を差し替えると、Rust 版を同じ検証にかけられます。

```powershell
$env:SANDS_EXE = "C:\Projects\HotKey\sands-rs\target\release\sands.exe"
tests\SandS.E2E\bin\Release\net9.0-windows\SandS.E2E.exe
```

テストは 2 部構成です。

- **Part 1**: 既定設定 (= 元の AHK スクリプト) が警告ゼロでコンパイルできること、
  トレイアイコンが生成できること、タスク XML が妥当なこと、スタートアップ登録の検証。
  C# 実装の内部を直接叩くので、`SANDS_EXE` 指定時は飛ばします (Rust 版では `cargo test` が同等を担当)。
- **Part 2**: 無害なキーだけを割り当てたテスト用設定での実挙動検証。
  実設定の `Win+1` / `Alt+F4` / `Ctrl+Win+F4` などは、テスト中に発火させるとウィンドウが飛んで
  テスト自体が壊れるため、実挙動としては流していません (修飾キー付きコンボの送出経路は
  `Ctrl+F13` などで担保しています)。

トラブル時は環境変数 `SANDS_DEBUG_LOG` に書き込み先パスを設定すると、
フックが受け取った生のキーイベントが記録されます (既定では完全に無効)。
