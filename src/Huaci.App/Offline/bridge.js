import { LatencyOptimisedTranslator } from "/engine/translator.js";

const translator = new LatencyOptimisedTranslator({
  registryUrl: "/models/index.json",
  pivotLanguage: null,
  downloadTimeout: 0,
  cacheSize: 64,
  useNativeIntGemm: false
});

const reply = message => window.chrome.webview.postMessage(message);
const supportedPairs = new Set(["en:zh", "zh:en"]);

window.chrome.webview.addEventListener("message", async event => {
  const request = event.data;
  if (!request || request.type !== "translate" || typeof request.id !== "string") {
    return;
  }

  try {
    const text = typeof request.text === "string" ? request.text.trim() : "";
    if (!text) {
      throw new Error("没有可翻译的文本。");
    }

    const from = typeof request.from === "string" ? request.from.toLowerCase() : "";
    const to = typeof request.to === "string" ? request.to.toLowerCase() : "";
    if (!supportedPairs.has(`${from}:${to}`)) {
      throw new Error("内置离线模型仅支持英语与简体中文互译。");
    }

    const response = await translator.translate({
      from,
      to,
      text,
      html: false,
      qualityScores: false
    });

    reply({
      type: "translation-result",
      id: request.id,
      ok: true,
      text: response.target.text
    });
  } catch (error) {
    reply({
      type: "translation-result",
      id: request.id,
      ok: false,
      error: error?.message || String(error)
    });
  }
});

window.addEventListener("unhandledrejection", event => {
  reply({
    type: "engine-error",
    error: event.reason?.message || String(event.reason)
  });
});

reply({ type: "ready" });
