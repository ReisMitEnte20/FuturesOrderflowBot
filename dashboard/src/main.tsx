import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import { Toaster } from "sonner";
import { ErrorBoundary } from "./components/common/ErrorBoundary";
import { ModalProvider } from "./components/common/Modal";
import { router } from "./router";
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