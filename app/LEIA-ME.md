# BDK Farm (app Android, Kotlin + Compose)

## Montar o projeto
1. Android Studio > New Project > **Empty Activity** (Compose). Nome `BDK Farm`, pacote `com.bdk.farm`, Minimum SDK 26.
2. Abra `app/src/main/java/com/bdk/farm/`. **Apague** o `MainActivity.kt` gerado e a pasta `ui/theme` (o app tem tema próprio).
3. Copie os 9 arquivos `.kt` de `com/bdk/farm/` deste zip para essa pasta.
4. Em `app/src/main/AndroidManifest.xml`, antes de `<application`, adicione:
   `<uses-permission android:name="android.permission.INTERNET" />`
   e dentro de `<application`, troque/adicione `android:allowBackup="false"`.
5. Sync do Gradle e Run (celular com depuração USB ou emulador).

## Gerar o APK para instalar
Build > Generate App Bundles or APKs > Generate APKs (debug serve). O arquivo fica em
`app/build/outputs/apk/debug/app-debug.apk`. Mande para o celular e instale (permita "fontes desconhecidas").

## Usar
Abra o app e cole o link do painel (`https://SEU-IP-COM-TRACOS.sslip.io/#token=...`).
O endereço precisa ser https. O token fica criptografado no Android Keystore.
