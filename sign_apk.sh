#!/bin/bash
# 签名 Android debug APK
# 用法：bash sign_apk.sh

JAVA="C:/Users/zion8/.workbuddy/binaries/java/jdk-21/bin/java.exe"
APKSIGNER="C:/Users/zion8/.workbuddy/binaries/android-sdk/build-tools/34.0.0/lib/apksigner.jar"
ZIPALIGN="C:/Users/zion8/.workbuddy/binaries/android-sdk/build-tools/34.0.0/zipalign.exe"
KEYSTORE="C:/Users/zion8/.android/debug.keystore"

APK="C:/Users/zion8/WorkBuddy/Wifi投屏/client/android/app/build/outputs/apk/debug/app-debug.apk"
ALIGNED="/tmp/screenmirror-aligned.apk"

echo "=== zipalign ==="
"$ZIPALIGN" -p 4 "$APK" "$ALIGNED"
echo "=== sign ==="
"$JAVA" -jar "$APKSIGNER" sign \
    --ks "$KEYSTORE" \
    --ks-pass pass:android \
    --ks-key-alias androiddebugkey \
    --key-pass pass:android \
    --out "$APK" \
    "$ALIGNED"
echo "=== verify ==="
"$JAVA" -jar "$APKSIGNER" verify "$APK"
echo "=== done ==="
rm -f "$ALIGNED"
