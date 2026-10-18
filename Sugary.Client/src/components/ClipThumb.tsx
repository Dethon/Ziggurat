import { useSettings } from "../settings";
import { Icon } from "./Icon";

export interface Clip {
  blob: Blob;
  url: string;
  // A clip's length, when known.
  seconds?: number;
}

// A clip among the photos, shown as she filmed it, with its length once it is known.
export function ClipThumb({
  clip,
  onSeconds,
  onRemove,
}: {
  clip: Clip;
  onSeconds: (seconds: number) => void;
  onRemove: () => void;
}) {
  const { t } = useSettings();
  return (
    <li>
      <video
        src={clip.url}
        muted
        playsInline
        preload="metadata"
        aria-label={clip.seconds ? t("clipSpoken", { s: clip.seconds }) : t("clip")}
        onLoadedMetadata={(event) => {
          // From the phone's camera app the length is read from the file, where it is known.
          const duration = event.currentTarget.duration;
          if (!clip.seconds && Number.isFinite(duration) && duration > 0) {
            onSeconds(Math.round(duration));
          }
        }}
      />
      <span className="clip-badge" aria-hidden="true">
        <Icon name="play" size={16} />
        {clip.seconds ? `${clip.seconds} s` : t("clip")}
      </span>
      <button type="button" className="photo-remove" aria-label={t("removeClip")} onClick={onRemove}>
        <Icon name="close" size={16} />
      </button>
    </li>
  );
}
