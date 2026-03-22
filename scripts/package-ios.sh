#!/bin/bash
# Package the iOS app for simulator deployment
# Usage: ./scripts/package-ios.sh [install]

set -e

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="Atlantis"
BUNDLE_ID="com.atlantis.app"
RID="iossimulator-arm64"
PROJECT_DIR="$REPO_ROOT/src/Atlantis"
ARTIFACTS_DIR="$REPO_ROOT/artifacts/publish/Atlantis/release_$RID"
OUTPUT_DIR="$REPO_ROOT/artifacts/app"
NATIVE_DIR="$REPO_ROOT/src/ios"
OBJ_DIR="$REPO_ROOT/artifacts/obj/swift"

# Determine target triple based on RID
if [[ "$RID" == "iossimulator-arm64" ]]; then
    SWIFT_TARGET="arm64-apple-ios26.0-simulator"
    SDK="iphonesimulator"
elif [[ "$RID" == "ios-arm64" ]]; then
    SWIFT_TARGET="arm64-apple-ios26.0"
    SDK="iphoneos"
else
    echo "Unsupported RID: $RID"
    exit 1
fi

SDK_PATH=$(xcrun --sdk "$SDK" --show-sdk-path)

# Compile Swift
echo "Compiling Swift for $SWIFT_TARGET..."
mkdir -p "$OBJ_DIR"
swiftc -target "$SWIFT_TARGET" \
    -sdk "$SDK_PATH" \
    -emit-object \
    -parse-as-library \
    -Osize \
    -Xlinker -rpath -Xlinker /usr/lib/swift \
    -o "$OBJ_DIR/AtlantisApp.o" \
    "$NATIVE_DIR/AtlantisApp.swift"

# Build .NET with Swift object linked
echo "Building for $RID..."
dotnet publish "$PROJECT_DIR" -r "$RID" -v quiet \
    -p:ExtraLinkerArgs="$OBJ_DIR/AtlantisApp.o"

# Create app bundle
echo "Creating $APP_NAME.app..."
mkdir -p "$OUTPUT_DIR/$APP_NAME.app"
cp "$ARTIFACTS_DIR/$APP_NAME" "$OUTPUT_DIR/$APP_NAME.app/"
cp "$NATIVE_DIR/Info.plist" "$OUTPUT_DIR/$APP_NAME.app/"

# Install to simulator if requested
if [ "$1" = "install" ]; then
    echo "Installing to simulator..."
    xcrun simctl install booted "$OUTPUT_DIR/$APP_NAME.app"
    xcrun simctl launch booted "$BUNDLE_ID"
fi
