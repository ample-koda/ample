exports.run = async () => {
  process.stdout.write('AMPLE VS Code test suite started\n');
  await require('./extension.test.js').run();
  process.stdout.write('AMPLE VS Code test suite passed\n');
};
