#!/bin/bash

# Change to the directory where the script is located
cd "$(dirname "$0")"

# Set the configuration to Release
CONFIG="Release"

# Set the relative path where you want to copy the DLL
# Adjust this path as needed
DEST_PATH="../../UnityProject/Trecs/Assets/com.trecs.core/Scripts/SourceGen"

# Build the project
dotnet build -c $CONFIG

# Check if the build was successful
if [ $? -eq 0 ]; then
    echo "Build successful. Copying DLL..."
    
    # Find the built DLL
    # Adjust the path if your output directory is different
    DLL_PATH="./Trecs.SourceGen/bin/$CONFIG/netstandard2.0/Trecs.SourceGen.dll"
    
    # Check if the DLL exists
    if [ -f "$DLL_PATH" ]; then
        # Create the destination directory if it doesn't exist
        mkdir -p "$DEST_PATH"
        
        # Copy the DLL
        cp "$DLL_PATH" "$DEST_PATH"
        
        if [ $? -eq 0 ]; then
            echo "DLL successfully copied to $DEST_PATH"
        else
            echo "Failed to copy DLL"
            exit 1
        fi
    else
        echo "DLL not found at $DLL_PATH"
        exit 1
    fi
else
    echo "Build failed"
    exit 1
fi
