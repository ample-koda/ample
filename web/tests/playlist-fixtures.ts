// Fixtures of the Playlist specs.
// A big song: 16 lines of seven blocks (notes, a synth with eight parts, three effects, a bus), the mixer and the master: 114 blocks over 200 bars.
export function bigSong() {
  const lines: string[] = ['composition "big" { ample 1  tempo 120  master-seed 7 }'];
  for (let i = 0; i < 16; i++) {
    const parts = Array.from({ length: 8 }, (_, k) => `${1 + k * 25} for 24`).join(', ');
    lines.push(
      `midi-clip n${i} {\n  bars 4\n  notes 1:0:1:60:90 2:0:1:64:90 3:0:1:67:90\n}`,
      `synth s${i} at ${parts} {\n  from n${i}.notes -> notes\n}`,
      `eq s${i}-eq {\n  from s${i}.audio -> audio\n}`,
      `compressor s${i}-comp {\n  from s${i}-eq.audio -> audio\n}`,
      `reverb s${i}-room {\n  from s${i}-comp.audio -> audio\n}`,
      `bus s${i}-bus {\n  from s${i}-room.audio -> audio1\n}`,
    );
  }
  lines.push(`mixer mix {\n  inputs 16\n${Array.from({ length: 16 }, (_, i) => `  from s${i}-bus.audio -> audio${i + 1}`).join('\n')}\n}`, 'master master {\n  from mix.audio -> audio\n}');
  return `${lines.join('\n')}\n`;
}
