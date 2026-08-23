namespace MobiFlight.Core.Web;

/// <summary>
/// The browser side shim that makes the unmodified MobiFlight frontend work outside WebView2.
/// </summary>
/// <remarks>
/// <para>
/// The frontend talks to its backend through two WebView2 APIs and nothing else:
/// <c>window.chrome.webview.postMessage(message)</c> to send, and
/// <c>window.chrome.webview.addEventListener("message", handler)</c> to receive, where
/// <c>event.data</c> is the <c>{ key, payload }</c> object.
/// </para>
/// <para>
/// Providing those two functions over a WebSocket is enough to run the production build in an
/// ordinary browser on macOS or Linux. The frontend needs no changes, which matters because it
/// keeps the Windows and portable builds on one codebase.
/// </para>
/// </remarks>
internal static class WebViewBridge
{
    /// <summary>
    /// The shim, injected into the served index.html ahead of the application bundle.
    /// </summary>
    public const string Script = """
        <script id="mobiflight-webview-bridge">
        (function () {
          if (window.chrome && window.chrome.webview) return; // Real WebView2, nothing to do.

          var listeners = [];
          var queue = [];
          var socket = null;

          function connect() {
            var protocol = location.protocol === "https:" ? "wss:" : "ws:";
            socket = new WebSocket(protocol + "//" + location.host + "/ws");

            socket.onopen = function () {
              console.log("[mobiflight] bridge connected");
              while (queue.length) socket.send(queue.shift());
            };

            socket.onmessage = function (event) {
              var parsed;
              try {
                parsed = JSON.parse(event.data);
              } catch (error) {
                console.error("[mobiflight] could not parse message", error);
                return;
              }

              // The frontend reads event.data, so hand it the object rather than the raw text.
              var delivered = new MessageEvent("message", { data: parsed });
              listeners.forEach(function (listener) {
                try {
                  listener(delivered);
                } catch (error) {
                  console.error("[mobiflight] handler failed", error);
                }
              });
            };

            socket.onclose = function () {
              console.warn("[mobiflight] bridge disconnected, retrying");
              setTimeout(connect, 1000);
            };

            socket.onerror = function () {
              if (socket) socket.close();
            };
          }

          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage: function (message) {
              var text = typeof message === "string" ? message : JSON.stringify(message);
              if (socket && socket.readyState === WebSocket.OPEN) socket.send(text);
              else queue.push(text);
            },
            addEventListener: function (type, handler) {
              if (type === "message") listeners.push(handler);
            },
            removeEventListener: function (type, handler) {
              if (type !== "message") return;
              var index = listeners.indexOf(handler);
              if (index >= 0) listeners.splice(index, 1);
            },
          };

          connect();
        })();
        </script>
        """;

    /// <summary>
    /// Inserts the shim into an HTML document so it runs before the application bundle.
    /// </summary>
    public static string Inject(string html)
    {
        if (html.Contains("mobiflight-webview-bridge", StringComparison.Ordinal)) return html;

        // Prefer the start of <head> so the shim exists before any module script executes.
        var headIndex = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIndex >= 0)
        {
            var insertAt = headIndex + "<head>".Length;
            return html[..insertAt] + "\n" + Script + html[insertAt..];
        }

        var htmlIndex = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlIndex >= 0)
        {
            var closeIndex = html.IndexOf('>', htmlIndex);
            if (closeIndex >= 0)
            {
                return html[..(closeIndex + 1)] + "\n" + Script + html[(closeIndex + 1)..];
            }
        }

        // No recognisable structure, so put it first and let the browser sort it out.
        return Script + "\n" + html;
    }
}
