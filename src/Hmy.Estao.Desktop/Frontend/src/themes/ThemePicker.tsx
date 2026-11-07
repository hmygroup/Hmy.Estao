import { themes, themeStyle } from "./index";
import { t } from "../i18n";

export default function ThemePicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  return <div className="theme-picker">
    {(["light", "dark"] as const).map(mode => <fieldset className="theme-group" key={mode}>
      <legend>{t(`theme.${mode}`)}</legend>
      <div className="theme-grid">{themes.filter(theme => theme.mode === mode).map(theme =>
        <label key={theme.id} className={`theme-option${value === theme.id ? " selected" : ""}`}>
          <input type="radio" name="desktop-theme" value={theme.id} checked={value === theme.id}
            onChange={() => onChange(theme.id)} aria-label={t("theme.option", t(`theme.${theme.id}`), t(`theme.mode.${mode}`))} />
          <div className="theme-swatch" data-mode={theme.mode} style={themeStyle(theme)} aria-hidden="true">
            <div className="theme-swatch-sidebar"><i /><i /><i /></div>
            <div className="theme-swatch-content"><i /><div><span /><b /></div><div><span /><b /></div></div>
          </div>
          <strong>{t(`theme.${theme.id}`)}</strong><small>{t(`theme.${theme.id}.description`)}</small>
        </label>)}</div>
    </fieldset>)}
  </div>;
}
