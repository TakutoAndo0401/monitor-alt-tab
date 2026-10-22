# Monitor Alt Tab

Windows 11向け、モニターごとのAlt＋Tab切り替えアプリ。C# / .NET 10 / WPF / Win32。個人用の初期版（0.1.1）です。

## 起動と使い方

配布フォルダーの `MonitorAltTab.exe` を起動します。インストーラー、管理者権限、追加ランタイムは不要です。初回は設定画面が開き、閉じてもタスクトレイに常駐します。Windowsへの自動起動登録は行いません。

- マウスポインターがある画面で **Alt＋Tab**：その画面のウィンドウを最近使った順で選択。
- Altを押したまま **Tab**：次へ。**Shift＋Tab**：前へ。
- **Altを離す**：選択したウィンドウへ切り替え。**Esc**：キャンセル。
- **Ctrl＋Alt＋F12**：一時停止／再開。ほかのアプリが同じキーを登録している場合は設定画面に表示します。
- タスクトレイのアイコンをダブルクリック：設定。右クリック：一時停止／終了。
- 終了するとキーフックが解除され、標準のAlt＋Tabに戻ります。

設定では、操作中のウィンドウのモニターを基準にする方式、プレビューのオン／オフ、ライト／ダーク／Windows追従、除外アプリを選べます。設定はEXEと同じ場所の `settings.json` に保存します。書き込み可能なユーザーフォルダーで使用してください。

## 対象と制約

- 対象画面は操作の開始時に固定。画面をまたぐウィンドウは面積が最大の画面に所属します。
- 最小化ウィンドウも対象。所属は最小化前の位置を使います。
- 現在の仮想デスクトップだけを対象にします。公開APIが使えない場合は置き換えを停止し、標準操作を通します。
- ブラウザーはウィンドウ単位です。ブラウザー内の各タブは列挙しません。
- 全画面アプリの操作中は初期設定で標準Alt＋Tabを使用。ゲーム、リモート接続、特殊なアプリは実行ファイル名で除外できます。
- タイトルバーのない特殊なウィンドウや所有関係のあるダイアログなどでは、標準Alt＋Tabの候補と完全には一致しません。
- 管理者アプリへの操作はWindowsの保護に従います。UAC、ロック、サインインの保護画面は対象外。常時管理者起動やUIAccessは使用しません。
- Windowsが前面化を拒否する場合は通知します。前面化を強制する操作は行いません。
- DWMプレビューは、アプリ・最小化・保護された画面内容によって表示できない／更新されない場合があります。
- キーフックが他のツールやWindowsによって解除される場合は、一度終了して起動し直してください。
- 選択中は切り替え画面自身がフォーカスを受け取り、対象へフォーカスを渡してから閉じます。高コントラストとアクセシビリティ名を備えますが、スクリーンリーダーでの選択読み上げは未検証です。
- 明示的な切り替え要求が拒否された場合は、マウスを移動・クリックしない空の入力通知を1回送って再試行します。Microsoft PowerToysの`WindowHelpers`でも使われる方式です。ほかのアプリとの入力キュー結合、管理者化、システム設定変更は行いません。

## プライバシー

アプリにネットワーク通信、更新確認、テレメトリー、キー入力履歴の保存はありません。低レベルキーフックでは修飾キーと操作対象キーの状態を処理します。ほかの入力内容を蓄積しません。候補のタイトル・アプリ名・パスは表示と除外のためメモリー内で扱います。プレビューを画像ファイルとして保存しません。

設定と、明示的に実行した診断・テストの結果だけをファイルに保存します。`--render-preview` は架空のウィンドウ名によるデザイン確認用画像を生成します。

EXEは未署名です。署名は発行元や改ざんを検証する仕組みで、安全性そのものの保証ではありません。MSIX化・外部への一般配布・証明書の信頼設定は行っていません。

## 開発

Windows x64 / .NET 10 SDK。検証SDKは10.0.402。外部NuGetライブラリは使用しません。Visual Studioは必須ではありません。

```powershell
dotnet restore --configfile NuGet.Config -p:NuGetAudit=false
dotnet build -c Release --no-restore
dotnet bin/Release/net10.0-windows/MonitorAltTab.dll --self-test
dotnet bin/Release/net10.0-windows/MonitorAltTab.dll --diagnose
dotnet bin/Release/net10.0-windows/MonitorAltTab.dll --activation-test
```

`build.ps1` でもビルドと自己テストを実行できます。`build.ps1 -Publish` はMicrosoft公式NuGetフィードから実行用パッケージを取得し、`publish` フォルダーへWindows x64の自己完結型EXEを作ります。初回の復元・ビルド時に.NET SDK自体がユーザー用キャッシュや設定ファイルを作る場合があります。

## 検証

`VALIDATION.md` を参照してください。自動テストは候補選択・キー状態遷移の検証です。実際のキーボード操作による切り替えの使用感、管理者アプリ、仮想デスクトップの切り替え、異なるDPI、スリープ／モニター抜き差しは実機での継続確認が必要です。

## 構成

`Native.cs`：Win32とCOMの宣言。`Catalog.cs`：候補の定期収集と最近使った順。`Model.cs`：設定・選択規則・キー状態。`KeyboardHook.cs`：専用スレッドとフック。`SwitcherWindow.cs`：切り替え画面とDWMプレビュー。`SettingsWindow.cs`：設定画面。`Program.cs`：常駐、切り替え、安全な終了。`ForegroundActivation.cs`：前面化要求。`SelfTests.cs`：自己テストと診断。`ActivationTests.cs`：別プロセスを使う前面化・復元の回帰検証。

前面化方式の参考：[Microsoft PowerToysのWindowHelpers](https://github.com/microsoft/PowerToys/blob/main/src/common/ManagedCommon/WindowHelpers.cs)、[SetForegroundWindowの制約](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)。
