export const languages = {
  fr: 'Fran\u00e7ais',
  en: 'English',
} as const;

export type Lang = keyof typeof languages;

export const defaultLang: Lang = 'fr';

export const ui = {
  fr: {
    // Nav
    'nav.products': 'Produits',
    'nav.vision': 'Vision',
    'nav.login': 'Connexion',
    'nav.dashboard': 'Tableau de bord',
    'nav.logout': 'D\u00e9connexion',

    // Hero
    'hero.badge': 'Disponible maintenant',
    'hero.badgeText': 'Serveur MCP pour Claude & Agents IA',
    'hero.title.line1': 'Arr\u00eatez d\u2019\u00e9diter vos documents Word.',
    'hero.title.line2': 'Laissez l\u2019IA le faire.',
    'hero.description': 'Votre \u00e9quipe passe des heures \u00e0 formater des rapports, mettre \u00e0 jour des contrats et corriger des documents \u00e0 la main. docx-mcp connecte vos assistants IA directement \u00e0 Microsoft Word\u00a0\u2014\u00a0pour qu\u2019ils lisent, \u00e9ditent et mettent en forme vos documents \u00e0 votre place.',
    'hero.cta.start': 'Commencer gratuitement',
    'hero.cta.roadmap': 'Voir la feuille de route',
    'hero.proof.claude': 'Compatible Claude Desktop',
    'hero.proof.opensource': 'Open Source',
    'hero.proof.license': 'Licence MIT',

    // Chat demo
    'chat.user': '\u00ab\u00a0Mets \u00e0 jour le rapport T4 avec les nouveaux chiffres de ventes et reformate le r\u00e9sum\u00e9 ex\u00e9cutif.\u00a0\u00bb',
    'chat.assistant': 'C\u2019est fait\u00a0! J\u2019ai mis \u00e0 jour les chiffres de vente dans les 12\u00a0tableaux et reformaté le r\u00e9sum\u00e9 ex\u00e9cutif avec les nouveaux faits marquants. Le document est sauvegard\u00e9.',
    'chat.file': 'Rapport-T4-2024.docx',

    // Problem
    'problem.title': 'L\u2019\u00e9dition de documents est encore manuelle. En 2024.',
    'problem.description': 'L\u2019IA sait \u00e9crire du code, analyser des donn\u00e9es et r\u00e9pondre \u00e0 des questions complexes. Mais pour vos documents Word\u00a0? Vous copiez, collez et formatez encore \u00e0 la main.',
    'problem.stat1.number': '5h+',
    'problem.stat1.label': 'par semaine pass\u00e9es \u00e0 formater des documents',
    'problem.stat2.number': '73%',
    'problem.stat2.label': 'des travailleurs du savoir \u00e9ditent des docs chaque jour',

    // Products
    'products.title': 'L\u2019\u00e9cosyst\u00e8me docx-mcp',
    'products.subtitle': 'Une mission\u00a0: rendre l\u2019\u00e9dition de documents automatique. Plusieurs produits pour y arriver.',
    'products.available': 'Disponible maintenant',
    'products.coming': 'Bient\u00f4t disponible',
    'products.mcp.title': 'MCP Server',
    'products.mcp.description': 'Connectez Claude, Cursor ou tout IA compatible MCP \u00e0 vos documents Word. Lire, \u00e9diter, formater\u00a0\u2014\u00a0tout en langage naturel.',
    'products.mcp.link': 'Voir sur GitHub',
    'products.addin.title': 'Word Add-in',
    'products.addin.description': '\u00c9dition assist\u00e9e par IA directement dans Microsoft Word. Disponible pour Windows, macOS et iPad.',
    'products.skill.title': 'Claude Skill',
    'products.skill.description': 'Utilisez les capacit\u00e9s de docx-mcp directement dans Claude.ai. Aucune installation\u00a0\u2014\u00a0demandez simplement \u00e0 Claude d\u2019\u00e9diter vos documents.',
    'products.skill.badge': 'Partenariat Anthropic',
    'products.workflows.title': 'Workflows documentaires agentiques',
    'products.workflows.description': 'Automatisation de bout en bout pour les pipelines documentaires d\u2019entreprise. Connectez vos documents \u00e0 des agents IA qui g\u00e8rent l\u2019int\u00e9gralit\u00e9 du cycle d\u2019\u00e9dition\u00a0\u2014\u00a0du brouillon \u00e0 l\u2019approbation finale.',

    // Vision
    'vision.title': 'L\u2019avenir de l\u2019\u00e9dition documentaire est agentique',
    'vision.description': 'Nous construisons un monde o\u00f9 vous n\u2019\u00e9ditez plus jamais un document Word manuellement. Votre assistant IA comprend votre intention, conna\u00eet vos documents et g\u00e8re le formatage, les mises \u00e0 jour et les r\u00e9visions\u00a0\u2014\u00a0pendant que vous vous concentrez sur l\u2019essentiel.',
    'vision.multiplatform.title': 'Multi-plateforme',
    'vision.multiplatform.description': 'Fonctionne partout o\u00f9 vivent vos documents\u00a0\u2014\u00a0Microsoft 365, SharePoint, fichiers locaux, stockage cloud.',
    'vision.ainative.title': 'IA-natif',
    'vision.ainative.description': 'Con\u00e7u pour Claude, ChatGPT et la prochaine g\u00e9n\u00e9ration d\u2019assistants IA.',
    'vision.enterprise.title': 'Pr\u00eat pour l\u2019entreprise',
    'vision.enterprise.description': 'S\u00e9curit\u00e9, conformit\u00e9 et piste d\u2019audit int\u00e9gr\u00e9es d\u00e8s le premier jour.',

    // CTA
    'cta.title': 'Pr\u00eat \u00e0 automatiser vos workflows documentaires\u00a0?',
    'cta.description': 'Commencez avec le MCP Server open source d\u00e8s aujourd\u2019hui. Soyez les premiers inform\u00e9s de la suite.',
    'cta.button': 'Commencer sur GitHub',
    'cta.note': 'Gratuit et open source sous licence MIT',

    // Footer
    'footer.tagline': '\u00c9dition documentaire assist\u00e9e par IA pour tous.',

    // Doccy
    'doccy.cta': 'Oui, montre-moi\u00a0!',
    'doccy.messages': [
      'Salut\u00a0! Je suis Doccy. On dirait que vous essayez d\u2019\u00e9diter un document Word. Vous voulez que l\u2019IA le fasse pour vous\u00a0?',
      'Saviez-vous\u00a0? Je peux mettre \u00e0 jour des tableaux, formater des titres et corriger vos r\u00e9sum\u00e9s automatiquement\u00a0!',
      'Marre du copier-coller\u00a0? Laissez-moi connecter votre assistant IA directement \u00e0 vos fichiers .docx\u00a0!',
      'Fun fact\u00a0: je peux annuler et r\u00e9tablir des modifications, comme un voyage dans le temps pour documents\u00a0!',
      'Besoin d\u2019\u00e9diter 100 rapports d\u2019un coup\u00a0? Je g\u00e8re \u00e7a avec les workflows agentiques\u00a0!',
      'Je fonctionne avec Claude, ChatGPT et tout IA compatible MCP. Pas mal, non\u00a0?',
      'Vos documents sur SharePoint\u00a0? Microsoft 365\u00a0? S3\u00a0? J\u2019arrive bient\u00f4t\u00a0!',
      'Word Add-in bient\u00f4t disponible\u00a0! Je serai directement dans Microsoft Word sur Windows, Mac et iPad.',
    ] as readonly string[],

    // Auth
    'auth.title': 'Connexion',
    'auth.github': 'Continuer avec GitHub',
    'auth.google': 'Continuer avec Google',
    'auth.microsoft': 'Continuer avec Microsoft',

    // Dashboard
    'dashboard.welcome': 'Bienvenue',
    'dashboard.storage': 'Stockage',
    'dashboard.documents': 'Documents',
    'dashboard.sessions': 'Sessions MCP',
    'dashboard.comingSoon': 'Bient\u00f4t disponible',
    'dashboard.tenantId': 'Identifiant tenant',
    'dashboard.gcsPrefix': 'Pr\u00e9fixe GCS',
    'dashboard.of': 'sur',
  },
  en: {
    // Nav
    'nav.products': 'Products',
    'nav.vision': 'Vision',
    'nav.login': 'Log in',
    'nav.dashboard': 'Dashboard',
    'nav.logout': 'Log out',

    // Hero
    'hero.badge': 'Available now',
    'hero.badgeText': 'MCP Server for Claude & AI Agents',
    'hero.title.line1': 'Stop editing Word documents.',
    'hero.title.line2': 'Let AI do it.',
    'hero.description': 'Your team spends hours formatting reports, updating contracts, and fixing documents manually. docx-mcp connects your AI assistants directly to Microsoft Word \u2014 so they can read, edit, and format documents for you.',
    'hero.cta.start': 'Get Started Free',
    'hero.cta.roadmap': 'See the Roadmap',
    'hero.proof.claude': 'Works with Claude Desktop',
    'hero.proof.opensource': 'Open Source',
    'hero.proof.license': 'MIT License',

    // Chat demo
    'chat.user': '"Update the Q4 report with new sales figures and format the executive summary."',
    'chat.assistant': 'Done! I\'ve updated the sales figures in all 12 tables and reformatted the executive summary with the new highlights. The document is saved.',
    'chat.file': 'Q4-Report-2024.docx',

    // Problem
    'problem.title': 'Document editing is still manual. In 2024.',
    'problem.description': 'AI can write code, analyze data, and answer complex questions. But when it comes to your Word documents? You\'re still copying, pasting, and formatting by hand.',
    'problem.stat1.number': '5h+',
    'problem.stat1.label': 'per week spent on document formatting',
    'problem.stat2.number': '73%',
    'problem.stat2.label': 'of knowledge workers edit docs daily',

    // Products
    'products.title': 'The docx-mcp Ecosystem',
    'products.subtitle': 'One mission: make document editing automatic. Multiple products to get there.',
    'products.available': 'Available now',
    'products.coming': 'Coming soon',
    'products.mcp.title': 'MCP Server',
    'products.mcp.description': 'Connect Claude, Cursor, or any MCP-compatible AI to your Word documents. Read, edit, format \u2014 all through natural language.',
    'products.mcp.link': 'View on GitHub',
    'products.addin.title': 'Word Add-in',
    'products.addin.description': 'AI-powered editing directly inside Microsoft Word. Available for Windows, macOS, and iPad.',
    'products.skill.title': 'Claude Skill',
    'products.skill.description': 'Use docx-mcp capabilities directly in Claude.ai. No setup, no installation \u2014 just ask Claude to edit your documents.',
    'products.skill.badge': 'Anthropic Partnership',
    'products.workflows.title': 'Agentic Document Workflows',
    'products.workflows.description': 'End-to-end automation for enterprise document pipelines. Connect your documents to AI agents that handle the entire editing lifecycle \u2014 from draft to final approval.',

    // Vision
    'vision.title': 'The future of document editing is agentic',
    'vision.description': 'We\'re building toward a world where you never manually edit a Word document again. Your AI assistant understands your intent, knows your documents, and handles the formatting, updates, and revisions \u2014 while you focus on what matters.',
    'vision.multiplatform.title': 'Multi-platform',
    'vision.multiplatform.description': 'Works everywhere your documents live \u2014 Microsoft 365, SharePoint, local files, cloud storage.',
    'vision.ainative.title': 'AI-native',
    'vision.ainative.description': 'Built for Claude, ChatGPT, and the next generation of AI assistants.',
    'vision.enterprise.title': 'Enterprise-ready',
    'vision.enterprise.description': 'Security, compliance, and audit trails built in from day one.',

    // CTA
    'cta.title': 'Ready to automate your document workflows?',
    'cta.description': 'Start with the open-source MCP Server today. Be first in line for what\'s next.',
    'cta.button': 'Get Started on GitHub',
    'cta.note': 'Free and open source under MIT License',

    // Footer
    'footer.tagline': 'AI-powered document editing for everyone.',

    // Doccy
    'doccy.cta': 'Yes, show me how!',
    'doccy.messages': [
      'Hi! I\'m Doccy. It looks like you\'re trying to edit a Word document. Would you like AI to do it for you?',
      'Did you know? I can update tables, format headings, and fix your executive summaries automatically!',
      'Tired of copy-pasting? Let me connect your AI assistant directly to your .docx files!',
      'Fun fact: I can undo and redo changes, just like time travel but for documents!',
      'Looking for a way to batch-edit 100 reports? I\'ve got you covered with agentic workflows!',
      'I work with Claude, ChatGPT, and any MCP-compatible AI. Pretty cool, right?',
      'Your documents on SharePoint? Microsoft 365? S3? I\'ll be there soon!',
      'Word Add-in coming soon! I\'ll be right inside Microsoft Word on Windows, Mac, and iPad.',
    ] as readonly string[],

    // Auth
    'auth.title': 'Log in',
    'auth.github': 'Continue with GitHub',
    'auth.google': 'Continue with Google',
    'auth.microsoft': 'Continue with Microsoft',

    // Dashboard
    'dashboard.welcome': 'Welcome',
    'dashboard.storage': 'Storage',
    'dashboard.documents': 'Documents',
    'dashboard.sessions': 'MCP Sessions',
    'dashboard.comingSoon': 'Coming soon',
    'dashboard.tenantId': 'Tenant ID',
    'dashboard.gcsPrefix': 'GCS prefix',
    'dashboard.of': 'of',
  },
} as const;
