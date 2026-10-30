# BDK Farm (app Android, Kotlin + Compose)

## Montar o projeto (primeira vez)
1. Android Studio > New Project > **Empty Activity** (Compose). Nome `BDK Farm`, pacote `com.bdk.farm`, Minimum SDK 26.
2. Abra `app/src/main/java/com/bdk/farm/`. **Apague** o `MainActivity.kt` gerado e a pasta `ui/theme` (o app tem tema próprio).
3. Copie todos os `.kt` de `com/bdk/farm/` deste zip para essa pasta.
4. Copie a pasta `res/` deste zip para `app/src/main/res/` (junta com a que já existe: `layout/`, `xml/`, `drawable/`).
5. Em `app/build.gradle.kts`, dentro de `dependencies { }`, adicione:
   `implementation("androidx.work:work-runtime-ktx:2.9.1")`
6. Em `app/src/main/AndroidManifest.xml`:
   - antes de `<application`:
     ```xml
     <uses-permission android:name="android.permission.INTERNET" />
     <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
     ```
   - em `<application`, deixe `android:allowBackup="false"`.
   - dentro de `<application>`, depois do `</activity>`:
     ```xml
     <receiver android:name=".FarmWidget" android:exported="false">
         <intent-filter>
             <action android:name="android.appwidget.action.APPWIDGET_UPDATE" />
         </intent-filter>
         <meta-data android:name="android.appwidget.provider" android:resource="@xml/farm_widget_info" />
     </receiver>
     ```
7. Sync do Gradle e Run.

## Atualizar um projeto que já existe
Substitua os `.kt` (há arquivos novos: `Achievements.kt`, `SettingsTab.kt`, `Background.kt`, `FarmWidget.kt`), copie a pasta `res/` e faça os passos 5 e 6 se ainda não fez.

## Gerar o APK
Build > Generate App Bundles or APKs > Generate APKs. O arquivo fica em `app/build/outputs/apk/debug/app-debug.apk`.

## Usar
Abra o app e cole o link do painel (`https://SEU-IP-COM-TRACOS.sslip.io/#token=...`). O token fica criptografado no Android Keystore.

- **Ajustes**: tema (Sistema, Claro, Escuro, Preto AMOLED) e cor de destaque; notificações no celular; widget; Discord; renovar login; trocar token.
- **Notificações**: o app confere o farm a cada 15/30/60 min mesmo fechado (WorkManager). O Android pode atrasar em modo economia; para avisos na hora, use o Discord.
- **Widget**: segure num espaço vazio da tela inicial › Widgets › BDK Farm, ou use o botão em Ajustes.
