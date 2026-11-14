// Which audition is the newest, and is it still the one the user asked for. Pure (no audio, no DOM), so it is tested in node. An audition takes a token when it starts
// (`begin`); Stop, Clear, a newer audition and a song that really opened call `invalidate`; the returned check is also false when the open song is no longer the one it began in.
export function createAuditionGuard(songOf) {
  let generation = 0;
  return {
    begin() {
      const token = ++generation, song = songOf();
      const check = () => token === generation && song === songOf();
      check.latest = () => token === generation;   // still the newest audition, whatever song is open (to tidy its own loading state)
      return check;
    },
    invalidate() { generation++; },
  };
}
