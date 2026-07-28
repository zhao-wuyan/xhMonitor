const port = Number(process.env.XHM_PROFILE_PORT ?? 45281);
const clients = new Set();

const server = Bun.serve({
  hostname: "127.0.0.1",
  port,
  fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === "/api/v1/config/health") {
      return Response.json({ status: "Healthy" });
    }
    if (url.pathname === "/api/v1/events") {
      let heartbeat;
      const stream = new ReadableStream({
        start(controller) {
          clients.add(controller);
          controller.enqueue(new TextEncoder().encode("event: connected\ndata: {}\n\n"));
          heartbeat = setInterval(() => {
            try {
              controller.enqueue(new TextEncoder().encode(": heartbeat\n\n"));
            } catch {
              clearInterval(heartbeat);
              clients.delete(controller);
            }
          }, 1000);
        },
        cancel() {
          clearInterval(heartbeat);
        },
      });
      return new Response(stream, {
        headers: {
          "content-type": "text/event-stream",
          "cache-control": "no-cache",
          connection: "keep-alive",
        },
      });
    }
    return new Response("not found", { status: 404 });
  },
});

console.log(`mock-sse-ready http://${server.hostname}:${server.port}`);
