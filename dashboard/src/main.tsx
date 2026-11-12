import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import { Toaster } from "sonner";
import { ErrorBoundary } from "./components/common/ErrorBoundary";
import { ModalProvider } from "./components/common/Modal";
import { router } from "./router";
// Schriften lokal gebündelt (kein Abruf von externen Font-Servern).
import "@fontsource/inter/400.css";
import "@fontsource/inter/500.css";
import "@fontsource/inter/600.css";
import "@fontsource/inter/700.css";
import "@fontsource/jetbrains-mono/400.css";
import "@fontsource/jetbrains-mono/500.css";
import "@fontsource/jetbrains-mono/700.css";
import "@fontsource/sora/600.css";
import "@fontsource/sora/700.css";
import "./index.css";

function App() {
  return (
    <ErrorBoundary>
      <ModalProvider>
        <RouterProvider router={router} />
        <Toaster position="bottom-right" richColors closeButton duration={3500} />
      </ModalProvider>
    </ErrorBoundary>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>
);