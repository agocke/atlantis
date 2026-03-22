#!/bin/bash
# Build and run XCUITests for Atlantis iOS app
# Usage: ./run-uitests.sh

set -e

cd "$(dirname "$0")"

# Use booted simulator or default to iPhone 15 Pro
DEVICE_ID=$(xcrun simctl list devices booted -j 2>/dev/null | grep -o '"udid" : "[^"]*"' | head -1 | cut -d'"' -f4)
DESTINATION="${DEVICE_ID:+id=$DEVICE_ID}"
DESTINATION="${DESTINATION:-name=iPhone 15 Pro}"

xcodebuild \
    -project AtlantisUITests.xcodeproj \
    -scheme AtlantisUITests \
    -destination "platform=iOS Simulator,$DESTINATION" \
    test \
    -quiet 2>&1 | grep -E "^Test|passed|failed|error:"
