import { createRoot } from "react-dom/client";
import App from "./App";
import "./styles.css";
import "./liquid-glass.css";
import { applyTheme, cachedTheme } from "./themes";

applyTheme(cachedTheme());

createRoot(document.getElementById("root")!).render(<App />);
