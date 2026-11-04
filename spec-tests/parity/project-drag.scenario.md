# Project drag and drop scenario

1. Upload an audio file into the project and drag its tile onto the canvas; expect an `import-audio` block referencing the existing project path.
2. Drag a file with an unaccepted extension onto a file parameter and verify it is rejected with visible feedback.
3. Drop a desktop file onto the Project panel and onto the picker; confirm it is journaled and appears without reloading.
4. Exercise duplicate resolution for desktop drops, including keeping both and recording an overwrite.
5. Drop a desktop file directly on the Project panel and verify upload progress reaches Complete before the refreshed tile appears.
5. Inspect lazy audio and MIDI thumbnails and verify preview metadata stays bounded and cached.
