#!/bin/bash

ROOT_DIR=$(dirname "$(realpath $0)")

# Deps
DEPS_DIR="$ROOT_DIR/dependencies/rust"
SHIPPED_DEPS_DIR="$DEPS_DIR/.shipped"

# Tools
TOOLS_DIR="$ROOT_DIR/tools"
AP_DIR="$TOOLS_DIR/AssemblyPublicizer"

# Vars
PENDING_PUBLICIZATION=()

echo "Cleaning up old publicized deps..."
rm $DEPS_DIR/*

echo "Publicizing All Dependencies"
echo "============================"
echo "Copying unpublicized dependencies..."

cd $SHIPPED_DEPS_DIR

for file in *; do
    if [[ "$file" == "Assembly-CSharp"* ]]; then
        PENDING_PUBLICIZATION=("${PENDING_PUBLICIZATION[@]}" "$file")
    elif [[ "$file" == "Facepunch."* ]]; then
        PENDING_PUBLICIZATION=("${PENDING_PUBLICIZATION[@]}" "$file")
    elif [[ "$file" == "Rust."* ]]; then
        PENDING_PUBLICIZATION=("${PENDING_PUBLICIZATION[@]}" "$file")
    else
        cp $SHIPPED_DEPS_DIR/$file $DEPS_DIR/$file
    fi
done

for i in "${PENDING_PUBLICIZATION[@]}"; do
    echo "Publicizing $SHIPPED_DEPS_DIR/$i"
    /usr/bin/mono $(realpath --relative-to="$SHIPPED_DEPS_DIR" $AP_DIR/AssemblyPublicizer.exe) -i $i -o $DEPS_DIR/$i

    if [ $? -ne 0 ]; then
        echo "Error: Failed to publicize $i. See error above."
        exit 1
    fi
done

echo "All dependencies have been publicized."
