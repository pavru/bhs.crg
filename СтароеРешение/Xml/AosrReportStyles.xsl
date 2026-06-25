<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet
	version='3.0'
	xmlns:xsl='http://www.w3.org/1999/XSL/Transform'
	xmlns:ct='urn:BimHouse:CommonDataType'
	xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'
	xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewElementResolverStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/PersonStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/OrgStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ConstructionObjectStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ListOfWorkStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ListOfMaterialStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ListOfDrawingStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/DocumentStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/AddressStyles.xsl'/>
	<xsl:output
		method='xml'
		version='1.0'
		encoding='UTF-8'
		indent='yes'/>
	<xsl:template match="processing-instruction()[name() = 'xml-stylesheet']"/>
	
	<xsl:template match='//ct:Подрядчик' mode="presenting">
		<xsl:next-match/>
		<xsl:if test="not(//ct:ОрганизацияВыполнившаяРаботы)">
			<xsl:variable name="DataToCopy">
				<xsl:next-match/>
			</xsl:variable>
			<xsl:element name="ОрганизацияВыполнившаяРаботы" namespace="urn:BimHouse:CommonDataType">
				<xsl:copy-of select="$DataToCopy/ct:Подрядчик/@*"/>
				<xsl:copy-of select="$DataToCopy/ct:Подрядчик/*"/>
			</xsl:element>
		</xsl:if>
	</xsl:template>
	<!--	<xsl:template match="//ct:Подрядчик" mode="presenting">
		<xsl:element name="Подрядчик" namespace="urn:BimHouse:CommonDataType">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./*" mode="#current"/>
		</xsl:element>
		<xsl:if test="not(//ct:ОрганизацияВыполнившаяРаботы)">
			<xsl:element name="ОрганизацияВыполнившаяРаботы" namespace="urn:BimHouse:CommonDataType">
				<xsl:copy-of select="@*"/>
				<xsl:apply-templates select="./*" mode="#current"/>
			</xsl:element>
		</xsl:if>
	</xsl:template>
-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлОбщихДанных')]" mode='presenting'/>
	<xsl:template match='ct:ДокументыСоответсвия | ct:Приложения' mode='presenting'>
		<xsl:variable name='Appendix' select='ct:Приложения'/>
		<xsl:element name='{name()}' namespace='{namespace-uri()}'>
			<xsl:copy-of select='@*'/>
			<xsl:variable name='Result'>
				<xsl:apply-templates select='./node()' mode='#current'/>
			</xsl:variable>
			<xsl:element name='ct:Документы' namespace='urn:BimHouse:CommonDataType'>
				<xsl:for-each select='$Result/*'>
					<xsl:copy-of select='./*'/>
				</xsl:for-each>
			</xsl:element>
			<xsl:element name='ct:Представления' namespace='urn:BimHouse:CommonDataType'>
				<xsl:for-each select='$Result/ct:Документы/*'>
					<xsl:element name='ct:Список' namespace='urn:BimHouse:CommonDataType'>
						<xsl:element name='ct:Текст' namespace='urn:BimHouse:CommonDataType'>
							<xsl:value-of select='position()'/>
							<xsl:text>. </xsl:text>
							<xsl:value-of select='ct:Представления/ct:ТипНомерДатаТитулДоп'/>
						</xsl:element>
					</xsl:element>
				</xsl:for-each>
			</xsl:element>
		</xsl:element>
	</xsl:template>
</xsl:stylesheet>