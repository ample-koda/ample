import { test } from 'node:test';
import assert from 'node:assert/strict';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { localOptions, midiArtifactOutputs } from '../analysis-local-transcription/runtime.mjs';

test('local transcription refuses missing tools and models without downloading', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-local-midi-'));
  try {
    const modelDirectory = path.join(folder, 'models');
    fs.mkdirSync(modelDirectory);
    const pythonPath = process.execPath;
    await assert.rejects(localOptions({ pythonPath, modelDirectory, separatorModel: 'missing.onnx' }), /never downloads models/);
    fs.writeFileSync(path.join(modelDirectory, 'model.onnx'), Buffer.from([1]));
    const options = await localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx', modelLicense: 'test-only' });
    assert.equal(options.pythonPath, pythonPath);
    assert.equal(options.modelDirectory, modelDirectory);
    assert.equal(options.separatorModel, 'model.onnx');
    assert.equal(options.modelLicense, 'test-only');
    assert.equal(options.drumMode, 'skip');
    assert.equal(options.computeDevice, 'gpu');
    assert.match(options.modelHash, /^[0-9a-f]{64}$/);
    await assert.rejects(localOptions({ pythonPath, modelDirectory, separatorModel: '../model.onnx' }), /model filename/);
    await assert.rejects(localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx;cmd' }), /model filename/);
    assert.equal((await localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx', drumMode: 'pitched' })).drumMode, 'pitched');
    await assert.rejects(localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx', drumMode: 'gm-percussion' }), /drumMode/);
    assert.equal((await localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx', computeDevice: 'cpu' })).computeDevice, 'cpu');
    await assert.rejects(localOptions({ pythonPath, modelDirectory, separatorModel: 'model.onnx', computeDevice: 'metal' }), /computeDevice/);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});

test('local transcription exposes bounded, separate backing MIDI stem artifacts', () => {
  const outputs = midiArtifactOutputs([
    { label: 'drums', path: 'out/backing-midi-01.mid' },
    { label: 'bass', path: 'out/backing-midi-02.mid' },
    { label: 'stem-03', path: 'out/backing-midi-03.mid' },
  ]);
  assert.deepEqual(outputs.map(({ role }) => role), ['backing-midi-drums-01', 'backing-midi-bass-02', 'backing-midi-stem-03-03']);
  assert.equal(midiArtifactOutputs([{ label: 'bass', path: 'out/bass.mid' }, { label: 'combined', path: 'out/backing-midi-combined.mid' }])[1].role, 'backing-midi-combined');
  assert.equal(midiArtifactOutputs([{ label: 'backing', path: 'out/backing-midi-01.mid' }])[0].role, 'backing-midi');
  assert.deepEqual(midiArtifactOutputs([]), []);
  assert.throws(() => midiArtifactOutputs([{ label: 'song-title', path: 'out/title.mid' }]), /invalid backing MIDI artifact/);
  assert.throws(() => midiArtifactOutputs([{ label: 'bass', path: 'out/../outside.mid' }]), /invalid backing MIDI artifact/);
  assert.throws(() => midiArtifactOutputs(Array.from({ length: 9 }, () => ({ label: 'bass', path: 'out/bass.mid' }))), /invalid number/);
});
