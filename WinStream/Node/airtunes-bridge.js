const path = require("path");

const prefix = "__WINSTREAM__";

function emit(message) {
  process.stdout.write(`${prefix}${JSON.stringify(message)}\n`);
}

function fail(message) {
  emit({ type: "error", message });
  process.exit(1);
}

const senderDir = process.env.WINSTREAM_NODE_AIRTUNES2_DIR;
if (!senderDir) {
  fail("WINSTREAM_NODE_AIRTUNES2_DIR is not set.");
}

let AirTunes;
try {
  AirTunes = require(path.join(senderDir, "lib"));
} catch (error) {
  fail(`Failed to load node_airtunes2 from ${senderDir}: ${error.message}`);
}

if (!process.argv[2]) {
  fail("Missing base64 configuration argument.");
}

let config;
try {
  const raw = Buffer.from(process.argv[2], "base64").toString("utf8");
  config = JSON.parse(raw);
} catch (error) {
  fail(`Failed to parse configuration payload: ${error.message}`);
}

const airtunes = new AirTunes();
const deviceKey = `${config.host}:${config.port || 5000}`;
let stopping = false;

airtunes.on("buffer", (status) => emit({ type: "buffer", status: status || "" }));
airtunes.on("device", (key, status, desc) => {
  const type =
    status === "ready"
      ? "ready"
      : status === "need_password"
        ? "need_password"
        : status === "pair_failed" || status === "error"
          ? "error"
          : "device";
  emit({
    type,
    key: key || "",
    status: status || "",
    message: desc || ""
  });
});

const args = {
  port: config.port || 5000,
  volume: config.volume || 50,
  txt: Array.isArray(config.txt) ? config.txt : [],
  airplay2: !!config.airplay2,
  debug: !!config.debug,
  forceAlac: config.forceAlac !== false
};

try {
  airtunes.add(config.host, args);
} catch (error) {
  fail(`Failed to add AirPlay device ${deviceKey}: ${error.message}`);
}

if (config.passcode) {
  setTimeout(() => {
    try {
      airtunes.setPasscode(deviceKey, String(config.passcode));
    } catch (error) {
      emit({ type: "error", message: `Failed to send passcode: ${error.message}` });
    }
  }, 250);
}

process.stdin.on("data", (data) => {
  try {
    airtunes.write(data);
  } catch (error) {
    emit({ type: "error", message: `Audio write failed: ${error.message}` });
  }
});

process.stdin.on("end", shutdown);
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);

function shutdown() {
  if (stopping) {
    return;
  }

  stopping = true;
  try {
    airtunes.end();
  } catch {
  }

  try {
    airtunes.stopAll(() => process.exit(0));
  } catch {
    process.exit(0);
  }

  setTimeout(() => process.exit(0), 1000);
}
