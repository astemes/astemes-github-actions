import argparse
import yaml
import excel_renderer.excel_renderer as excel_renderer
import nav_generator.nav_generator as nav_generator
from pathlib import Path
from datetime import date
from functools import reduce


def generate_mkdocs_astemes_yaml_file(directory, 
site_name="Site Name", 
site_url="http://example.com",
repo_url="", 
author="Author Name",
copyright_year=0,
hide_generator_notice=False):
    builder = Mkdocs_Builder()
    mkdocs_file = Path(directory).parent / 'mkdocs.yml'
    with open(mkdocs_file, 'w') as file:
        builder.add_plugin("search")
        builder.add_plugin("with-pdf")
        builder.set_theme("material")
        builder.set_site_name(site_name)
        builder.set_site_author(author)
        builder.set_pdf_cover_subtitle("Astemes - "+author)
        current_year=str(date.today().year)
        builder.set_copyright("Copyright © "+(str(copyright_year)+" - "+current_year if (copyright_year != 0 and str(copyright_year) != current_year) else current_year)+" Astemes")
        builder.set_site_url(site_url)
        builder.set_repo_url(repo_url)
        builder.set_docs_dir(Path(directory).name)
        builder.set_nav(nav_generator.Nav_Generator().generate_nav(directory))
        builder.set_logo("Style/logo.png")
        builder.set_favicon("Style/logo.png")
        builder.set_css("Style/style.css")
        builder.set_pdf_template_path("Style")
        builder.add_plugin_option("with-pdf","output_path", site_name+".pdf")
        builder.add_plantuml()
        builder.hide_generator_notice(hide_generator_notice)
        builder.add_theme_feature("navigation.indexes")
        yaml.dump(builder.yaml,file)
    return "Generated " + str(mkdocs_file.resolve())

def use_readme_as_index(docs_directory):
    readme_file_path = list(Path(docs_directory).parent.glob("[rR][eE][aA][dD][mM][eE].[mM][dD]"))
    for readme in readme_file_path:
        index_file_path = Path(docs_directory)/"README.md"
        with open(readme,"r") as f:
            index_content = f.read()
        with open(index_file_path,"w+") as f:
            f.write(index_content)

class Mkdocs_Builder:
    def __init__(self) -> None:
        self.yaml = {}

    def set_site_name(self,site_name):
        self.yaml.update({"site_name": site_name})

    def set_site_author(self,site_author):
        self.yaml.update({"site_author": site_author})
        
    def set_copyright(self,copyright):
        self.yaml.update({"copyright": copyright})     

    def set_site_url(self,site_url):
        self.yaml.update({"site_url": site_url})
    
    def set_repo_url(self,repo_url):
        self.yaml.update({"repo_url": repo_url})

    def set_docs_dir(self,docs_dir):
        self.yaml.update({"docs_dir": docs_dir})

    def set_nav(self,nav):
        self.yaml.update({"nav": nav})

    def set_theme(self,theme):
        self._update_theme('name',theme)

    def add_theme_feature(self,feature):
        self._update_theme_list('features',feature)
    
    def set_css(self,css):
        try:
            css_list = self.yaml['extra_css']
        except KeyError:
            css_list = []
        css_list.append(css)
        self.yaml.update({'extra_css':css_list})
    
    def set_pdf_template_path(self, template_path):
        if self._plugin_is_set("with-pdf"):
            self.add_plugin_option("with-pdf","custom_template_path",template_path)


    def set_logo(self, logo):
        self._update_theme('logo',logo)
        if self._plugin_is_set("with-pdf"):
            self.add_plugin_option("with-pdf","cover_logo", logo)

    def set_pdf_cover_subtitle(self, subtitle_text):
        self.add_plugin_option('with-pdf','cover_subtitle',subtitle_text)
    
    def set_favicon(self, favicon):
        self._update_theme('favicon',favicon)

    def set_version_extra(self, key, value):
        if value:
            try:
                self.yaml["extra"]["version"].update({key: value})
            except KeyError:
                try:
                    self.yaml["extra"].update({"version": {key: value}})
                except KeyError:
                    self.yaml.update({"extra": {"version": {key: value}}})

    def hide_generator_notice(self, hidden):
        try:
            self.yaml["extra"].update({"generator": not hidden})
        except KeyError:
            self.yaml.update({"extra": {"generator": not hidden}})

    def enable_darkmode(self):
        self.yaml.update( {'palette': [{'media': '(prefers-color-scheme: light)', 'scheme': 'default', 'toggle': {'icon': 'material/toggle-switch-off-outline', 'name': 'Switch to dark mode'}}, {'media': '(prefers-color-scheme: dark)', 'scheme': 'slate', 'toggle': {'icon': 'material/toggle-switch', 'name': 'Switch to light mode'}}]})
            
    def add_plugin(self,plugin):
        try:
            self.yaml["plugins"].append(plugin)
        except KeyError:
            self.yaml.update({"plugins": [plugin]})

    def add_plugin_option(self,plugin,option,value):
        if not self._plugin_is_set(plugin):
            self.add_plugin(plugin)
        plugin_list = self.yaml["plugins"]
        plugin_list = [self._update_or_create_dict(item,plugin,option,value) if self._compare_string_or_dict(item,plugin) else item for item in plugin_list]
        self.yaml["plugins"]= plugin_list

    def add_markdown_extension(self, extension):
        try:
            self.yaml["markdown_extensions"].append(extension)
        except KeyError:
            self.yaml.update({"markdown_extensions": [extension]})

    def add_markdown_extension_option(self,extension,option,value):
        if not self._markdown_extension_is_set(extension):
            self.add_markdown_extension(extension)
        extension_list = self.yaml["markdown_extensions"]
        extension_list = [self._update_or_create_dict(item,extension,option,value) if self._compare_string_or_dict(item,extension) else item for item in extension_list]
        self.yaml["markdown_extensions"]= extension_list


    def add_plantuml(self):
        self.add_markdown_extension("plantuml_markdown")
        self.add_markdown_extension_option("plantuml_markdown","server","http://www.plantuml.com/plantuml")

    def _update_or_create_dict(self,dict_or_string,key,sub_key,value):
        if type(dict_or_string) == str:
            return {key: {sub_key: value}} 
        if type(dict_or_string) == dict:
            dict_or_string[key].update({sub_key:value})
            return dict_or_string

    def _compare_string_or_dict(self, string_or_dict, value):
        if type(string_or_dict) == str:
            return value == string_or_dict
        if type(string_or_dict) == dict:
            return value in string_or_dict.keys()

    def _update_theme(self,key,value):
        if value:
            try:
                self.yaml["theme"].update({key: value})
            except KeyError:
                self.yaml.update({"theme": {key: value}})
    
    def _update_theme_list(self,key,list_element):
        if list_element:
            try:
                current_list = self.yaml["theme"][key]
                current_list.append(list_element)
                self.yaml["theme"].update({key: current_list})
                self.yaml=self.yaml
            except KeyError:
                try:
                    self.yaml["theme"][key]= [list_element]
                except KeyError:
                    self.yaml.update({"theme": {key: [list_element]}})

    def _plugin_is_set(self,plugin):
        try:
            plugin_list = self.yaml["plugins"]
        except KeyError:
            return False
        for item in plugin_list:
            if plugin == item:
                return True
            elif type(item) == dict:
                if plugin in item.keys():
                    return True
        return False

    def _markdown_extension_is_set(self,extension):
        try:
            extension_list = self.yaml["markdown_extensions"]
        except KeyError:
            return False
        for item in extension_list:
            if extension == item:
                return True
            elif type(item) == dict:
                if extension in item.keys():
                    return True
        return False


if __name__=="__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--site_name", help="The name of the project.",default="Template Project")
    parser.add_argument("--repo_url", help="The url to the GitHub repository.",default="https://github.com/astemes")
    parser.add_argument("--docs_path", help="The path to the folder containing the documents to build.",default="docs")
    parser.add_argument("--author", help="Author of the code base.",default="Anton Sundqvist")
    parser.add_argument("--initial_release", help="The year the project was started, used for copyright notice.", type=int)
    parser.add_argument("--render_excel_files",help="Set to false to disable rendering of excel files as markdown", type=bool, default=True)
    args = parser.parse_args()
    docs_path = args.docs_path
    use_readme_as_index(docs_path)
    excel_renderer.render_excel_as_markdown(docs_path)
    docs_path=Path(docs_path)
    file_name = generate_mkdocs_astemes_yaml_file(docs_path,
        site_name=args.site_name,
        repo_url=args.repo_url,
        site_url="http://astemes.com",
        author=args.author,
        copyright_year=args.initial_release)
    print(file_name)