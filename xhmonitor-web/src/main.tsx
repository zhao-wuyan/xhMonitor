import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './index.css';
import App from './App.tsx';
import { FloatingWidget, TaskbarWidget } from './pages/DesktopWidget';

// 根据 URL 路径渲染不同的组件
const getComponent = () => {
  const path = window.location.pathname;

  if (path === '/widget/floating') {
    return <FloatingWidget />;
  } else if (path === '/widget/taskbar') {
    return <TaskbarWidget />;
  } else {
    return <App />;
  }
};

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {getComponent()}
  </StrictMode>,
);
