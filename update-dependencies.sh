#!/bin/bash

OS=$1
BRANCH=$2

if [ $# -lt 2 ]; then
  echo "Error: This script requires 2 arguments."
  echo "Usage: $0 <os> <branch>"
  exit 1
fi

VALID_OS=("windows" "linux")
VALID_BRANCH=("public" "release" "staging")

APP_ID=258550
RUST_DEP_FILELIST_FILE="rust-dependency-filelist.txt"

# Root
ROOT_DIR=$(dirname "$(realpath $0)")

# Deps
DEPS_DIR="$ROOT_DIR/dependencies/rust"
TEMP_DEPS_DIR="$DEPS_DIR/.temp"
SHIPPED_DEPS_DIR="$DEPS_DIR/.shipped"

# Tools
TOOLS_DIR="$ROOT_DIR/tools"
DD_DIR="$TOOLS_DIR/DepotDownloader"

# Validate OS and Branch inputs.
if [[ ! "${VALID_OS[@]}" =~ "${OS}" ]]; then
    echo "Error: The OS you entered is not valid. Valid options are windows or linux."
    exit 1
fi

if [[ ! "${VALID_BRANCH[@]}" =~ "${BRANCH}" ]]; then
    echo "Error: The branch you entered is not valid. Valid options are public, release, or staging."
    exit 1
fi

# Validate all required directories and files exist.
if [ ! -d "${TOOLS_DIR}" ]; then
    echo "Error: The tools directory could not be found."
    exit 1
fi

if [[ ! -f "$DD_DIR/DepotDownloader" ]]; then
    echo "Error: Could not find the DepotDownloader binary."
    exit 1
fi

if [[ ! -f "$DD_DIR/$RUST_DEP_FILELIST_FILE" ]]; then
    echo "Error: Could not find the rust dependency filelist."
    exit 1
fi

# Create all necessary directories.

echo "Deleting all old deps..."
rm -r $DEPS_DIR/*

if [[ ! -d {$TEMP_DEPS_DIR} ]]; then
    echo "Creating temp deps directory..."
    mkdir -p $TEMP_DEPS_DIR
fi

if [[ ! -d {$SHIPPED_DEPS_DIR} ]]; then
    echo "Creating shipped deps directory..."
    mkdir -p $SHIPPED_DEPS_DIR
fi

echo "Downloading all new deps..."
$DD_DIR/DepotDownloader -app $APP_ID -os $OS -branch $BRANCH -filelist "$DD_DIR/$RUST_DEP_FILELIST_FILE" -dir "$TEMP_DEPS_DIR"

echo "Moving dependencies from temp directory to shipped directory."
mv $TEMP_DEPS_DIR/RustDedicated_Data/Managed/* $SHIPPED_DEPS_DIR

echo "Deleting temp directories and files."
rm -r $TEMP_DEPS_DIR

if [ $? -eq 0 ]; then
    echo "Download completed without errors."
else 
    echo "Error: An error occurred while downloading the dependencies."
    exit 1
fi

echo "Dependencies have been successfully updated."
exit 0
