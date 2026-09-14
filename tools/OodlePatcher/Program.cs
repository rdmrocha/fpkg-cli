// Thin process entry point. The patch lives in OodlePatcherProgram so that `fpkg patch`
// can run it in-process, without the SDK the distribution zip deliberately does not need.
return OodlePatcher.OodlePatcherProgram.Run(args);
