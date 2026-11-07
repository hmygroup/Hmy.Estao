import { createRoot } from "react-dom/client";
import App from "./App";
import "./styles.css";
import "./liquid-glass.css";
import "./harness-hub.css";
import { applyTheme, cachedTheme } from "./themes";
import { currentLanguage } from "./i18n";

applyTheme(cachedTheme());
document.documentElement.lang = currentLanguage();

createRoot(document.getElementById("root")!).render(<App />);
