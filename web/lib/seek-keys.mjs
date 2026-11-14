// Seeking a waveform with the keyboard: the new position in seconds for a key press, kept between 0 and the length; null for a key that does not seek.
// Left and Right move 2 seconds (10 with Shift), Home and End go to the ends. Pure, so node --test checks it.
export function seekByKey(current, duration, key, shift = false) {
  const step = shift ? 10 : 2;
  let next;
  if (key === 'ArrowRight') next = current + step;
  else if (key === 'ArrowLeft') next = current - step;
  else if (key === 'Home') next = 0;
  else if (key === 'End') next = duration;
  else return null;
  return Math.max(0, Math.min(duration, next));
}
