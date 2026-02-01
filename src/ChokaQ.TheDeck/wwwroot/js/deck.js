window.deck = {
    scrollToBottom: (element) => {
        if (element) {
            element.scrollIntoView({ behavior: 'smooth', block: 'end' });
        }
    },
    setTheme: (theme) => {
        document.documentElement.setAttribute('data-theme', theme);
    }
};
