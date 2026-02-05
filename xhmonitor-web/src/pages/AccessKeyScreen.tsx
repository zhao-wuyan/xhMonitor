import { useEffect, useRef, useState } from 'react';
import { getAccessKey, setAccessKey } from '../config/accessKey';
import { t } from '../i18n';

export const AccessKeyScreen = () => {
  const [value, setValue] = useState(() => getAccessKey());
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    inputRef.current?.focus();
    inputRef.current?.select();
  }, []);

  const submit = () => {
    setAccessKey(value);
  };

  return (
    <div className="access-key-screen" role="main">
      <div className="access-key-screen__content">
        <div className="access-key-screen__title">{t('Access Key Required')}</div>
        <input
          ref={inputRef}
          type="password"
          className="access-key-screen__input"
          value={value}
          autoComplete="off"
          placeholder={t('Enter access key')}
          onChange={(event) => setValue(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') submit();
          }}
        />
        <div className="access-key-screen__hint">{t('Access key hint')}</div>
      </div>
    </div>
  );
};
